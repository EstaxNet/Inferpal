// Inferpal chat webview. The extension host owns the transcript; this script only renders
// state pushed via postMessage and reports user intents back. It must survive being
// destroyed on hide: everything re-renders from the 'hydrate' message.
// No literal user-visible English here — strings go through t() (window.__l10n).
import type {
  ExtToWebview,
  WebviewToExt,
  WvBackendStatus,
  WvChip,
  WvMentionCategory,
  WvMentionItem,
  WvPlan,
  WvSlashCommand,
  WvTranscriptItem,
} from '../webviewMessages';
import { t } from './l10n';
import { renderMarkdownInto, setCopySink } from './markdown';
import { renderXray, setXraySink } from './xray';

const vscode = acquireVsCodeApi();
const post = (msg: WebviewToExt) => vscode.postMessage(msg);
setCopySink((text) => post({ type: 'copyText', text }));
setXraySink((msg) => post(msg));

const topbarEl = document.getElementById('topbar')!;
const messagesEl = document.getElementById('messages')!;
const composerEl = document.getElementById('composer')!;
const planEl = document.getElementById('plan')!;
const statusEl = document.getElementById('statusline')!;
const promptEl = document.getElementById('prompt') as HTMLTextAreaElement;
const toolbarEl = document.getElementById('toolbar')!;
const footerEl = document.getElementById('footerbar')!;

// ── Local state (rebuilt from hydrate) ───────────────────────────────────────
let busy = false;
let agentMode = true;
let currentModel = '';
let streamEl: HTMLElement | null = null; // live assistant bubble while tokens stream
let streamRaw = '';
let transcriptEmpty = true;
let slashCommands: WvSlashCommand[] = [];
let toolBubblesExpanded = false;
let contextWindow = 0;

// ── Topbar: connection badge + VRAM + search toggle ─────────────────────────
const connDot = document.createElement('span');
connDot.id = 'connDot';
const connText = document.createElement('span');
connText.id = 'connText';
const connRetry = document.createElement('button');
connRetry.id = 'connRetry';
connRetry.textContent = '↻';
connRetry.title = t('retry');
connRetry.hidden = true;
connRetry.addEventListener('click', () => post({ type: 'retryConnection' }));
const vramEl = document.createElement('span');
vramEl.id = 'vram';
vramEl.hidden = true;
const topSpacer = document.createElement('span');
topSpacer.className = 'spacer';
const searchToggle = document.createElement('button');
searchToggle.id = 'searchToggle';
searchToggle.textContent = '🔍';
searchToggle.title = t('searchTitle');
topbarEl.append(connDot, connText, connRetry, vramEl, topSpacer, searchToggle);

// Search bar: dims non-matching bubbles (VS parity: grisées, pas masquées).
const searchBar = document.createElement('div');
searchBar.id = 'searchbar';
searchBar.hidden = true;
const searchInput = document.createElement('input');
searchInput.id = 'searchInput';
searchInput.placeholder = t('searchPlaceholder');
const searchClear = document.createElement('button');
searchClear.textContent = '✕';
searchBar.append(searchInput, searchClear);
topbarEl.insertAdjacentElement('afterend', searchBar);

function applySearch(): void {
  const q = searchInput.value.trim().toLowerCase();
  for (const el of messagesEl.querySelectorAll<HTMLElement>('.bubble')) {
    el.classList.toggle('search-dim', q.length > 0 && !(el.textContent ?? '').toLowerCase().includes(q));
  }
}
searchToggle.addEventListener('click', () => {
  searchBar.hidden = !searchBar.hidden;
  if (!searchBar.hidden) {
    searchInput.focus();
  } else {
    searchInput.value = '';
    applySearch();
  }
});
searchClear.addEventListener('click', () => {
  searchInput.value = '';
  applySearch();
  searchInput.focus();
});
searchInput.addEventListener('input', applySearch);

function setBackendStatus(status: WvBackendStatus | null): void {
  if (!status) {
    connDot.className = '';
    connText.textContent = '';
    connRetry.hidden = true;
    vramEl.hidden = true;
    return;
  }
  connDot.className = status.connected ? 'ok' : 'ko';
  connText.textContent = status.connected ? t('statusConnected') : t('statusUnreachable');
  connRetry.hidden = status.connected;
  vramEl.hidden = status.vramBadge.length === 0;
  vramEl.textContent = status.vramBadge ? 'VRAM ' + status.vramBadge : '';
  vramEl.title = status.vramBadge;
}

// ── Toolbar: attach menu + model picker + mode toggle + send/stop ───────────
const plusBtn = document.createElement('button');
plusBtn.id = 'plus';
plusBtn.textContent = '+';
const plusMenu = document.createElement('div');
plusMenu.id = 'plusmenu';
plusMenu.hidden = true;

function buildPlusMenu(): void {
  plusMenu.textContent = '';
  const entries: { label: string; msg: WebviewToExt }[] = [
    { label: t('attachActiveFile'), msg: { type: 'attachActive' } },
    { label: t('attachSelection'), msg: { type: 'attachSelection' } },
    { label: t('attachBrowse'), msg: { type: 'attachBrowse' } },
  ];
  for (const entry of entries) {
    const item = document.createElement('div');
    item.className = 'popup-item';
    item.textContent = entry.label;
    item.addEventListener('mousedown', (e) => {
      e.preventDefault();
      plusMenu.hidden = true;
      post(entry.msg);
    });
    plusMenu.appendChild(item);
  }
}
plusBtn.addEventListener('click', () => {
  plusMenu.hidden = !plusMenu.hidden;
  if (!plusMenu.hidden) {
    buildPlusMenu();
  }
});
document.addEventListener('mousedown', (e) => {
  if (!plusMenu.hidden && e.target !== plusBtn && !plusMenu.contains(e.target as Node)) {
    plusMenu.hidden = true;
  }
});

const modelEl = document.createElement('select');
modelEl.id = 'model';
modelEl.title = t('modelTitle');
modelEl.addEventListener('change', () => {
  currentModel = modelEl.value;
  post({ type: 'pickModel', model: modelEl.value });
});
const modeBtn = document.createElement('button');
modeBtn.id = 'mode';
modeBtn.addEventListener('click', () => post({ type: 'toggleAgentMode' }));
const sendBtn = document.createElement('button');
sendBtn.id = 'send';
sendBtn.addEventListener('click', () => {
  if (busy) {
    post({ type: 'cancel' });
  } else {
    send();
  }
});
plusBtn.title = t('attachMenuTitle');
toolbarEl.append(plusBtn, modelEl, modeBtn, sendBtn);
composerEl.insertBefore(plusMenu, promptEl);

function applyAgentMode(enabled: boolean): void {
  agentMode = enabled;
  modeBtn.textContent = enabled ? t('modeAgent') : t('modeChat');
  modeBtn.title = t('modeToggleTitle');
  modeBtn.classList.toggle('agent', enabled);
  renderWelcomeFooter();
}

function setBusy(value: boolean): void {
  busy = value;
  sendBtn.textContent = value ? '■' : '↑';
  sendBtn.title = value ? t('cancelTitle') : t('sendTitle');
  sendBtn.classList.toggle('stop', value);
  if (!value) {
    statusEl.hidden = true;
    statusEl.textContent = '';
  }
}

// ── Footer: hint · token info · context gauge (clickable → X-Ray) ───────────
const hintEl = document.createElement('span');
hintEl.id = 'hint';
hintEl.textContent = t('hintBar');
const tokensEl = document.createElement('span');
tokensEl.id = 'tokens';
const ctxBar = document.createElement('div');
ctxBar.id = 'ctxbar';
ctxBar.hidden = true;
const ctxFill = document.createElement('div');
ctxFill.id = 'ctxfill';
ctxBar.appendChild(ctxFill);
ctxBar.addEventListener('click', () => post({ type: 'openXray' }));
footerEl.append(hintEl, tokensEl, ctxBar);

/** Same thresholds/colors as the VS ContextBudgetGauge (50/80/95%). */
function updateGauge(promptTokens: number, lastTokens: number): void {
  tokensEl.textContent = lastTokens > 0 ? t('tokensInfo', lastTokens.toLocaleString()) : '';
  if (contextWindow <= 0 || promptTokens <= 0) {
    ctxBar.hidden = true;
    return;
  }
  const pct = Math.min(100, (promptTokens * 100) / contextWindow);
  ctxFill.style.width = pct + '%';
  ctxFill.style.background = pct < 50 ? '#606060' : pct < 80 ? '#C0A000' : pct < 95 ? '#D06000' : '#CC2222';
  ctxBar.title = t('contextTooltip', promptTokens.toLocaleString(), contextWindow.toLocaleString(), pct.toFixed(0));
  ctxBar.hidden = false;
}

// ── Status line / plan block ─────────────────────────────────────────────────
function setStatus(text: string): void {
  statusEl.textContent = text;
  statusEl.hidden = !text;
}

function renderPlan(plan: WvPlan | null): void {
  planEl.textContent = '';
  planEl.hidden = !plan;
  if (!plan) {
    return;
  }
  const goal = document.createElement('div');
  goal.className = 'plan-goal';
  goal.textContent = '◈ ' + plan.goal;
  planEl.appendChild(goal);
  for (const step of plan.steps) {
    const row = document.createElement('div');
    const s = step.status.toLowerCase();
    const icon = s.includes('done') || s.includes('completed') ? '✓'
      : s.includes('running') || s.includes('progress') || s.includes('active') ? '▸'
      : s.includes('fail') || s.includes('error') || s.includes('skip') ? '✗'
      : '○';
    row.className = 'plan-step ' + (icon === '✓' ? 'done' : icon === '▸' ? 'running' : icon === '✗' ? 'failed' : 'pending');
    row.textContent = icon + ' ' + step.text;
    planEl.appendChild(row);
  }
}

// ── Bubbles ──────────────────────────────────────────────────────────────────
function scrollToBottom(): void {
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

function metaRow(item: { text: string; timestamp?: string }, copyText?: string): HTMLElement {
  const meta = document.createElement('div');
  meta.className = 'bubble-meta';
  if (item.timestamp) {
    const time = document.createElement('span');
    time.className = 'bubble-time';
    time.textContent = item.timestamp;
    meta.appendChild(time);
  }
  const copy = document.createElement('button');
  copy.className = 'bubble-action';
  copy.textContent = '⧉';
  copy.title = t('copy');
  copy.addEventListener('click', () => post({ type: 'copyText', text: copyText ?? item.text }));
  meta.appendChild(copy);
  return meta;
}

function addBubble(role: string, item: WvTranscriptItem): HTMLElement {
  hideWelcome();
  const el = document.createElement('div');
  el.className = 'bubble ' + role;
  const body = document.createElement('div');
  body.className = 'bubble-body';
  renderMarkdownInto(body, item.text);
  el.appendChild(body);
  el.appendChild(metaRow(item));
  messagesEl.appendChild(el);
  scrollToBottom();
  return el;
}

/** Collapsible tool bubble: header 🔧 name (+ error badge), body input+output. */
function addToolBubble(item: WvTranscriptItem, expanded: boolean): void {
  hideWelcome();
  const el = document.createElement('div');
  el.className = 'bubble tool' + (item.hasErrors ? ' tool-error' : '');

  const header = document.createElement('div');
  header.className = 'tool-header';
  const chevron = document.createElement('span');
  chevron.className = 'tool-chevron';
  const name = document.createElement('code');
  name.textContent = item.text;
  header.append(chevron, document.createTextNode('🔧 '), name);
  if (item.hasErrors) {
    const err = document.createElement('span');
    err.className = 'tool-errbadge';
    err.textContent = t('toolError');
    header.appendChild(err);
    // "Fix with AI": pre-fills the prompt with the failing output (VS parity).
    const fix = document.createElement('button');
    fix.className = 'bubble-action';
    fix.textContent = '🛠 ' + t('fixWithAi');
    fix.addEventListener('click', (e) => {
      e.stopPropagation(); // don't toggle the bubble
      promptEl.value = t('fixPrompt') + '\n\n```\n' + (item.toolOutput ?? '') + '\n```';
      promptEl.focus();
    });
    header.appendChild(fix);
  }
  if (item.timestamp) {
    const time = document.createElement('span');
    time.className = 'bubble-time';
    time.textContent = item.timestamp;
    header.appendChild(time);
  }
  el.appendChild(header);

  const body = document.createElement('div');
  body.className = 'tool-body';
  if (item.toolInput) {
    const inp = document.createElement('pre');
    inp.className = 'tool-input';
    inp.textContent = item.toolInput;
    body.appendChild(inp);
  }
  if (item.toolOutput) {
    const out = document.createElement('pre');
    out.className = 'tool-output';
    out.textContent = item.toolOutput;
    body.appendChild(out);
  }
  el.appendChild(body);

  // Errors always start expanded — the red output is the point of the bubble.
  let open = expanded || item.hasErrors === true;
  const apply = () => {
    body.hidden = !open;
    chevron.textContent = open ? '▾' : '▸';
  };
  apply();
  header.addEventListener('click', () => {
    open = !open;
    apply();
  });

  messagesEl.appendChild(el);
  scrollToBottom();
}

function ensureStreamBubble(): HTMLElement {
  if (!streamEl) {
    hideWelcome();
    streamEl = document.createElement('div');
    streamEl.className = 'bubble assistant streaming';
    const body = document.createElement('div');
    body.className = 'bubble-body';
    streamEl.appendChild(body);
    messagesEl.appendChild(streamEl);
    streamRaw = '';
  }
  return streamEl;
}

function finishStream(): void {
  if (streamEl) {
    streamEl.classList.remove('streaming');
    streamEl = null;
    streamRaw = '';
  }
}

/** Offers a regenerate button under the newest assistant bubble only. */
function refreshRegenerate(): void {
  for (const old of messagesEl.querySelectorAll('.bubble-regen')) {
    old.remove();
  }
  const bubbles = messagesEl.querySelectorAll<HTMLElement>('.bubble.assistant');
  const last = bubbles.length > 0 ? bubbles[bubbles.length - 1] : null;
  if (!last || busy) {
    return;
  }
  const btn = document.createElement('button');
  btn.className = 'bubble-action bubble-regen';
  btn.textContent = '↺ ' + t('regenerate');
  btn.addEventListener('click', () => post({ type: 'regenerate' }));
  last.querySelector('.bubble-meta')?.appendChild(btn);
}

// ── Approval card (unchanged behavior: 3-way answer + open-as-diff) ─────────
function renderApprovalMessage(message: string): HTMLElement {
  const pre = document.createElement('pre');
  pre.className = 'approval-text';
  for (const line of message.split('\n')) {
    const span = document.createElement('span');
    span.textContent = line + '\n';
    if (/^\+(?!\+\+)/.test(line)) {
      span.className = 'diff-add';
    } else if (/^-(?!--)/.test(line)) {
      span.className = 'diff-del';
    }
    pre.appendChild(span);
  }
  return pre;
}

function addApprovalCard(id: number, message: string): void {
  finishStream();
  hideWelcome();
  const card = document.createElement('div');
  card.className = 'bubble approval';
  const text = document.createElement('div');
  text.className = 'approval-message';
  text.appendChild(renderApprovalMessage(message));
  const actions = document.createElement('div');
  actions.className = 'approval-actions';
  const buttons = [
    { label: t('deny'), answer: 0, cls: 'deny' },
    { label: t('allowOnce'), answer: 1, cls: 'allow' },
    { label: t('allowAlways'), answer: 2, cls: 'allow' },
  ];
  for (const spec of buttons) {
    const btn = document.createElement('button');
    btn.textContent = spec.label;
    btn.className = spec.cls;
    btn.addEventListener('click', () => {
      post({ type: 'approvalAnswer', id, answer: spec.answer });
      card.classList.add('answered');
      for (const b of actions.querySelectorAll('button')) {
        (b as HTMLButtonElement).disabled = true;
      }
      btn.classList.add('chosen');
    });
    actions.appendChild(btn);
  }
  const openBtn = document.createElement('button');
  openBtn.textContent = '⧉';
  openBtn.title = t('openInEditor');
  openBtn.addEventListener('click', () => post({ type: 'openApprovalDiff', text: message }));
  actions.appendChild(openBtn);
  card.appendChild(text);
  card.appendChild(actions);
  messagesEl.appendChild(card);
  scrollToBottom();
}

// ── Welcome screen (VS parity: ◇ Inferpal + 4 action cards) ─────────────────
const welcomeEl = document.createElement('div');
welcomeEl.id = 'welcome';
welcomeEl.hidden = true;
messagesEl.appendChild(welcomeEl);
const welcomeFooter = document.createElement('div');

function buildWelcome(): void {
  welcomeEl.textContent = '';
  const title = document.createElement('div');
  title.className = 'welcome-title';
  title.textContent = '◇ Inferpal';
  const subtitle = document.createElement('div');
  subtitle.className = 'welcome-subtitle';
  subtitle.textContent = t('welcomeSubtitle');
  welcomeEl.append(title, subtitle);

  const cards = document.createElement('div');
  cards.className = 'welcome-cards';
  const specs = [
    { emoji: '⚡', label: t('cardExplain'), cmd: '/explain' },
    { emoji: '🐛', label: t('cardFix'), cmd: '/fix' },
    { emoji: '🧪', label: t('cardTest'), cmd: '/test' },
    { emoji: '❓', label: t('cardHelp'), cmd: '/help' },
  ];
  for (const spec of specs) {
    const card = document.createElement('button');
    card.className = 'welcome-card';
    const emoji = document.createElement('div');
    emoji.className = 'welcome-emoji';
    emoji.textContent = spec.emoji;
    const label = document.createElement('div');
    label.textContent = spec.label;
    card.append(emoji, label);
    card.addEventListener('click', () => post({ type: 'send', text: spec.cmd }));
    cards.appendChild(card);
  }
  welcomeEl.appendChild(cards);

  welcomeFooter.className = 'welcome-footer';
  welcomeEl.appendChild(welcomeFooter);
  renderWelcomeFooter();
}

function renderWelcomeFooter(): void {
  welcomeFooter.textContent =
    (currentModel ? currentModel + ' · ' : '') + (agentMode ? t('modeAgent') : t('modeChat'));
}

function showWelcomeIfEmpty(): void {
  welcomeEl.hidden = !transcriptEmpty;
  if (!welcomeEl.hidden) {
    buildWelcome();
  }
}

function hideWelcome(): void {
  transcriptEmpty = false;
  welcomeEl.hidden = true;
}

// ── Popups above the composer: @-mentions and slash autocomplete ────────────
interface Popup {
  el: HTMLElement;
  index: number;
  items: string[];
}

function makePopup(id: string): Popup {
  const el = document.createElement('div');
  el.id = id;
  el.className = 'composer-popup';
  el.hidden = true;
  composerEl.prepend(el);
  return { el, index: 0, items: [] };
}

// Two-level typed mentions (VS parity): '@' opens the category menu (8 categories from the
// host + free file suggestions), a committed '@file/@code/@folder ' drills into a sub-search.
const mentions = makePopup('mentions');
let mentionCategories: WvMentionCategory[] = [];
let mentionStart = -1; // offset of '@' in the textarea, -1 = popup closed
let mentionActions: (() => void)[] = [];
let mentionDebounce: ReturnType<typeof setTimeout> | undefined;

const COMMITTED_RE = /@(file|code|folder) ([^\n@]*)$/i;
const TYPING_RE = /@([\w.]*)$/;

function closeMentions(): void {
  mentions.el.hidden = true;
  mentions.el.textContent = '';
  mentionStart = -1;
  mentions.index = 0;
  mentionActions = [];
  if (mentionDebounce) {
    clearTimeout(mentionDebounce);
  }
}

function openMentionRows(rows: { build: (row: HTMLElement) => void; action: () => void }[]): void {
  if (rows.length === 0) {
    closeMentions();
    return;
  }
  mentions.el.textContent = '';
  mentionActions = rows.map((r) => r.action);
  mentions.items = rows.map(() => '');
  mentions.index = Math.min(mentions.index, rows.length - 1);
  rows.forEach((spec, i) => {
    const row = document.createElement('div');
    row.className = 'popup-item mention-item' + (i === mentions.index ? ' selected' : '');
    spec.build(row);
    row.addEventListener('mousedown', (e) => {
      e.preventDefault(); // keep textarea focus
      spec.action();
    });
    mentions.el.appendChild(row);
  });
  mentions.el.hidden = false;
}

function detectMention(): void {
  const caret = promptEl.selectionStart;
  const before = promptEl.value.slice(0, caret);

  const committed = before.match(COMMITTED_RE);
  if (committed) {
    mentionStart = caret - committed[0].length;
    const category = committed[1].toLowerCase();
    const query = committed[2];
    if (category === 'code') {
      // Semantic search has no intermediate hits: one action row running the query.
      openMentionRows(query.trim().length === 0 ? [] : [{
        build: (row) => { row.textContent = '🔮 ' + t('mentionSearchCode', query); },
        action: () => {
          stripMentionToken();
          post({ type: 'resolveMention', category: 'code', value: query.trim() });
        },
      }]);
      return;
    }
    if (mentionDebounce) {
      clearTimeout(mentionDebounce);
    }
    mentionDebounce = setTimeout(() => post({ type: 'mentionSearch', category, query }), 120);
    return;
  }

  const typing = before.match(TYPING_RE);
  if (!typing) {
    closeMentions();
    return;
  }
  mentionStart = caret - typing[0].length;
  renderMentionCategories(typing[1].toLowerCase());
  // Free file suggestions under the categories, like the VS popup's open-file list.
  post({ type: 'mentionQuery', query: typing[1] });
}

function renderMentionCategories(partial: string): void {
  const matches = mentionCategories.filter((c) => c.token.slice(1).startsWith(partial));
  openMentionRows(matches.map((c) => ({
    build: (row) => {
      const tok = document.createElement('span');
      tok.className = 'slash-cmd';
      tok.textContent = c.token;
      const desc = document.createElement('span');
      desc.className = 'slash-hint';
      desc.textContent = c.description;
      row.append(tok, desc);
    },
    action: () => selectMentionCategory(c),
  })));
}

function selectMentionCategory(c: WvMentionCategory): void {
  if (c.queryBased) {
    // Commit '@file ' etc. so the user types the sub-query.
    const caret = promptEl.selectionStart;
    const value = promptEl.value;
    const before = value.slice(0, caret).replace(TYPING_RE, c.token + ' ');
    promptEl.value = before + value.slice(caret);
    promptEl.setSelectionRange(before.length, before.length);
    promptEl.focus();
    detectMention(); // @folder lists everything on an empty query
    return;
  }
  stripMentionToken();
  post({ type: 'resolveMention', category: c.token.slice(1) });
}

/** Removes the trailing @mention token (committed or bare) from the prompt. */
function stripMentionToken(): void {
  const caret = promptEl.selectionStart;
  const value = promptEl.value;
  const before = value.slice(0, caret).replace(COMMITTED_RE, '').replace(TYPING_RE, '').trimEnd();
  promptEl.value = before + value.slice(caret);
  promptEl.setSelectionRange(before.length, before.length);
  promptEl.focus();
  closeMentions();
}

function insertMention(path: string): void {
  const caret = promptEl.selectionStart;
  const value = promptEl.value;
  promptEl.value = value.slice(0, mentionStart) + '@' + path + ' ' + value.slice(caret);
  const pos = mentionStart + path.length + 2;
  promptEl.setSelectionRange(pos, pos);
  promptEl.focus();
  closeMentions();
}

/** Free file suggestions for a bare '@…' (open editors + workspace glob) — appended
 * under the category rows so both stay reachable. */
function renderMentions(items: string[]): void {
  if (mentionStart < 0) {
    return;
  }
  const caret = promptEl.selectionStart;
  const typing = promptEl.value.slice(0, caret).match(TYPING_RE);
  if (!typing) {
    return;
  }
  const partial = typing[1].toLowerCase();
  const categories = mentionCategories.filter((c) => c.token.slice(1).startsWith(partial));
  openMentionRows([
    ...categories.map((c) => ({
      build: (row: HTMLElement) => {
        const tok = document.createElement('span');
        tok.className = 'slash-cmd';
        tok.textContent = c.token;
        const desc = document.createElement('span');
        desc.className = 'slash-hint';
        desc.textContent = c.description;
        row.append(tok, desc);
      },
      action: () => selectMentionCategory(c),
    })),
    ...items.map((path) => ({
      build: (row: HTMLElement) => { row.textContent = path; },
      action: () => insertMention(path),
    })),
  ]);
}

/** @file / @folder sub-search results pushed back by the extension. */
function renderMentionResults(category: string, items: WvMentionItem[]): void {
  if (mentionStart < 0) {
    return;
  }
  openMentionRows(items.map((item) => ({
    build: (row: HTMLElement) => {
      const label = document.createElement('span');
      label.className = 'slash-cmd';
      label.textContent = item.label;
      const detail = document.createElement('span');
      detail.className = 'slash-hint';
      detail.textContent = item.detail;
      row.append(label, detail);
    },
    action: () => {
      if (category === 'file') {
        // Files stay in the prompt as '@rel/path' — expanded into an attachment at send.
        const caret = promptEl.selectionStart;
        const value = promptEl.value;
        const rel = item.detail.replace(/\\/g, '/');
        const before = value.slice(0, caret).replace(COMMITTED_RE, '@' + rel + ' ');
        promptEl.value = before + value.slice(caret);
        promptEl.setSelectionRange(before.length, before.length);
        promptEl.focus();
        closeMentions();
      } else {
        stripMentionToken();
        post({ type: 'resolveMention', category, value: item.value });
      }
    },
  })));
}

// ── Context chips + "+" attach menu ──────────────────────────────────────────
const chipsEl = document.createElement('div');
chipsEl.id = 'chips';
chipsEl.hidden = true;
composerEl.insertBefore(chipsEl, promptEl);

function renderChips(chips: WvChip[]): void {
  chipsEl.textContent = '';
  chipsEl.hidden = chips.length === 0;
  chips.forEach((chip, i) => {
    const el = document.createElement('span');
    el.className = 'chip';
    const name = document.createElement('span');
    name.textContent = chip.name;
    const close = document.createElement('button');
    close.textContent = '✕';
    close.title = t('chipRemove');
    close.addEventListener('click', () => post({ type: 'removeChip', index: i }));
    el.append(name, close);
    chipsEl.appendChild(el);
  });
}

const slash = makePopup('slash');

function closeSlash(): void {
  slash.el.hidden = true;
  slash.el.textContent = '';
  slash.items = [];
  slash.index = 0;
}

/** Autocomplete on a spaceless "/prefix" (same trigger as the VS popup). */
function detectSlash(): void {
  const text = promptEl.value;
  if (!text.startsWith('/') || text.includes(' ') || text.includes('\n') || slashCommands.length === 0) {
    closeSlash();
    return;
  }
  const matches = slashCommands.filter((c) => c.command.toLowerCase().startsWith(text.toLowerCase())).slice(0, 12);
  if (matches.length === 0) {
    closeSlash();
    return;
  }
  slash.el.textContent = '';
  slash.items = matches.map((m) => m.command);
  slash.index = Math.min(slash.index, matches.length - 1);
  matches.forEach((m, i) => {
    const row = document.createElement('div');
    row.className = 'popup-item slash-item' + (i === slash.index ? ' selected' : '');
    const cmd = document.createElement('span');
    cmd.className = 'slash-cmd';
    cmd.textContent = m.command;
    const hint = document.createElement('span');
    hint.className = 'slash-hint';
    hint.textContent = m.hint;
    row.append(cmd, hint);
    row.addEventListener('mousedown', (e) => {
      e.preventDefault();
      applySlash(m.command);
    });
    slash.el.appendChild(row);
  });
  slash.el.hidden = false;
}

function applySlash(command: string): void {
  promptEl.value = command + ' ';
  promptEl.setSelectionRange(promptEl.value.length, promptEl.value.length);
  promptEl.focus();
  closeSlash();
}

/** Shared ↑/↓/Tab/Enter/Escape navigation for a popup; true when the key was consumed. */
function popupKey(popup: Popup, e: KeyboardEvent, apply: (item: string) => void, close: () => void): boolean {
  if (popup.el.hidden) {
    return false;
  }
  const rows = popup.el.querySelectorAll('.popup-item');
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    popup.index = (popup.index + (e.key === 'ArrowDown' ? 1 : rows.length - 1)) % rows.length;
    rows.forEach((r, i) => r.classList.toggle('selected', i === popup.index));
    return true;
  }
  if (e.key === 'Tab' || e.key === 'Enter') {
    e.preventDefault();
    apply(popup.items[popup.index]);
    return true;
  }
  if (e.key === 'Escape') {
    e.preventDefault();
    close();
    return true;
  }
  return false;
}

// ── Prompt history (Shift+↑↓) — webview mirror of the VS PromptHistoryNavigator ──
let historyEntries: string[] = []; // most-recent-last, fed by the extension
let historyIndex = -1; // -1 = not navigating
let historyDraft = '';

function historyUp(): void {
  if (historyEntries.length === 0) {
    return;
  }
  if (historyIndex === -1) {
    historyDraft = promptEl.value;
  }
  historyIndex = Math.min(historyIndex + 1, historyEntries.length - 1);
  promptEl.value = historyEntries[historyEntries.length - 1 - historyIndex];
}

function historyDown(): void {
  if (historyIndex < 0) {
    return;
  }
  historyIndex--;
  promptEl.value = historyIndex >= 0 ? historyEntries[historyEntries.length - 1 - historyIndex] : historyDraft;
  if (historyIndex < 0) {
    historyDraft = '';
  }
}

// ── Composer events ──────────────────────────────────────────────────────────
function send(): void {
  const text = promptEl.value;
  if (!text.trim() || busy) {
    return;
  }
  closeMentions();
  closeSlash();
  historyIndex = -1;
  historyDraft = '';
  promptEl.value = '';
  post({ type: 'send', text });
}

promptEl.placeholder = t('promptPlaceholder');
promptEl.addEventListener('input', () => {
  detectMention();
  detectSlash();
  historyIndex = -1; // editing resets history navigation, like the VS guard
});
promptEl.addEventListener('blur', () => setTimeout(() => { closeMentions(); closeSlash(); }, 150));
/** Keyboard navigation of the mention popup — rows carry their own actions. */
function mentionKey(e: KeyboardEvent): boolean {
  if (mentions.el.hidden) {
    return false;
  }
  const rows = mentions.el.querySelectorAll('.popup-item');
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    mentions.index = (mentions.index + (e.key === 'ArrowDown' ? 1 : rows.length - 1)) % rows.length;
    rows.forEach((r, i) => r.classList.toggle('selected', i === mentions.index));
    return true;
  }
  if (e.key === 'Tab' || e.key === 'Enter') {
    e.preventDefault();
    mentionActions[mentions.index]?.();
    return true;
  }
  if (e.key === 'Escape') {
    e.preventDefault();
    closeMentions();
    return true;
  }
  return false;
}

promptEl.addEventListener('keydown', (e) => {
  if (popupKey(slash, e, applySlash, closeSlash)) {
    return;
  }
  if (mentionKey(e)) {
    return;
  }
  // Shift+↑↓ prompt history — only while the prompt is single-line (VS parity:
  // multi-line falls through to caret movement).
  if (e.shiftKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown') && !promptEl.value.includes('\n')) {
    e.preventDefault();
    if (e.key === 'ArrowUp') {
      historyUp();
    } else {
      historyDown();
    }
    return;
  }
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    send();
  }
});

// ── State ← extension ────────────────────────────────────────────────────────
function renderTranscript(transcript: WvTranscriptItem[]): void {
  messagesEl.textContent = '';
  messagesEl.appendChild(welcomeEl);
  streamEl = null;
  transcriptEmpty = true;
  for (const item of transcript) {
    if (item.role === 'tool') {
      addToolBubble(item, toolBubblesExpanded);
    } else if (item.role === 'user' || item.role === 'assistant' || item.role === 'error') {
      addBubble(item.role, item);
    }
  }
  transcriptEmpty = transcript.length === 0;
  showWelcomeIfEmpty();
  refreshRegenerate();
  applySearch();
}

window.addEventListener('message', (event: MessageEvent<ExtToWebview>) => {
  const msg = event.data;
  switch (msg.type) {
    case 'hydrate': {
      // Defensive defaults: survive a stale extension↔webview pair (in-place update).
      slashCommands = msg.commands ?? [];
      toolBubblesExpanded = msg.toolBubblesExpanded === true;
      contextWindow = msg.contextWindow ?? 0;
      historyEntries = msg.history ?? [];
      currentModel = msg.model ?? '';
      mentionCategories = msg.mentionCategories ?? [];
      renderChips(msg.chips ?? []);
      renderTranscript(msg.transcript ?? []);

      modelEl.textContent = '';
      for (const name of msg.models) {
        const opt = document.createElement('option');
        opt.value = name;
        opt.textContent = name;
        opt.selected = name === msg.model;
        modelEl.appendChild(opt);
      }
      if (msg.model && !msg.models.includes(msg.model)) {
        const opt = document.createElement('option');
        opt.value = msg.model;
        opt.textContent = msg.model;
        opt.selected = true;
        modelEl.appendChild(opt);
      }
      applyAgentMode(msg.agentMode);
      setBackendStatus(msg.status);
      updateGauge(msg.promptTokens, msg.lastTokens);
      renderPlan(msg.plan);
      setBusy(msg.busy);
      if (msg.busy && msg.stream) {
        const el = ensureStreamBubble();
        streamRaw = msg.stream;
        renderMarkdownInto(el.querySelector('.bubble-body') as HTMLElement, streamRaw);
      }
      break;
    }
    case 'turnStarted':
      historyEntries = msg.history;
      addBubble('user', { role: 'user', text: msg.prompt, timestamp: msg.timestamp });
      renderPlan(null);
      setBusy(true);
      refreshRegenerate();
      break;
    case 'token': {
      const el = ensureStreamBubble();
      streamRaw += msg.text;
      renderMarkdownInto(el.querySelector('.bubble-body') as HTMLElement, streamRaw);
      scrollToBottom();
      break;
    }
    case 'thinking':
      setStatus(t('thinking'));
      break;
    case 'status':
      setStatus(msg.text);
      break;
    case 'tool':
      finishStream();
      addToolBubble(
        {
          role: 'tool',
          text: msg.name,
          toolInput: msg.input,
          toolOutput: msg.output,
          hasErrors: msg.hasErrors,
          timestamp: msg.timestamp,
        },
        msg.expanded,
      );
      break;
    case 'plan':
      renderPlan(msg.plan);
      break;
    case 'approval':
      addApprovalCard(msg.id, msg.message);
      break;
    case 'mentionSuggestions':
      renderMentions(msg.items);
      break;
    case 'xrayPanel':
      renderXray(msg.panel);
      break;
    case 'streamReset':
      if (streamEl) {
        streamEl.remove();
        streamEl = null;
        streamRaw = '';
      }
      break;
    case 'backendStatus':
      setBackendStatus(msg.status);
      break;
    case 'agentMode':
      applyAgentMode(msg.enabled);
      break;
    case 'setPrompt':
      promptEl.value = msg.text;
      promptEl.focus();
      promptEl.setSelectionRange(promptEl.value.length, promptEl.value.length);
      break;
    case 'mentionResults':
      renderMentionResults(msg.category, msg.items);
      break;
    case 'chips':
      renderChips(msg.chips);
      break;
    case 'stepPaused': {
      finishStream();
      hideWelcome();
      const pause = document.createElement('div');
      pause.className = 'bubble assistant step-pause';
      const body = document.createElement('div');
      body.className = 'bubble-body';
      body.textContent = t('stepPaused');
      const resume = document.createElement('button');
      resume.className = 'bubble-action';
      resume.textContent = '▶ ' + t('resume');
      resume.addEventListener('click', () => post({ type: 'resumeStep' }));
      pause.append(body, resume);
      messagesEl.appendChild(pause);
      scrollToBottom();
      break;
    }
    case 'stepResumed':
      for (const el of messagesEl.querySelectorAll('.step-pause')) {
        el.remove();
      }
      break;
    case 'turnEnded': {
      if (streamEl) {
        // Replace the stream bubble content with the authoritative final text.
        streamRaw = msg.text || streamRaw;
        renderMarkdownInto(streamEl.querySelector('.bubble-body') as HTMLElement, streamRaw);
        streamEl.appendChild(metaRow({ text: streamRaw, timestamp: msg.timestamp }));
        finishStream();
      } else if (msg.text) {
        addBubble('assistant', { role: 'assistant', text: msg.text, timestamp: msg.timestamp });
      }
      if (msg.error) {
        addBubble('error', { role: 'error', text: msg.error, timestamp: msg.timestamp });
      }
      if (msg.cancelled) {
        addBubble('error', { role: 'error', text: t('cancelled'), timestamp: msg.timestamp });
      }
      renderPlan(null);
      setBusy(false);
      updateGauge(msg.promptTokens, msg.tokens);
      refreshRegenerate();
      scrollToBottom();
      break;
    }
  }
});

post({ type: 'ready' });
