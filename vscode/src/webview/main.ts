// Inferpal chat webview. The extension host owns the transcript; this script only renders
// state pushed via postMessage and reports user intents back. It must survive being
// destroyed on hide: everything re-renders from the 'hydrate' message.
// No literal user-visible English here — strings go through t() (window.__l10n).
import type { ApprovalCard, RunSummary } from '../protocol';
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
import { renderMarkdownInto, setCopySink, stripThinkTags } from './markdown';
import { renderXray, setXraySink } from './xray';
import { icon, iconButton, setIcon, type IconName } from './icons';
import { mentionFor } from '../mentionPaths';

const vscode = window.__vsapi ?? acquireVsCodeApi();
const post = (msg: WebviewToExt) => vscode.postMessage(msg);
setCopySink((text) => post({ type: 'copyText', text }));
setXraySink((msg) => post(msg));

const topbarEl = document.getElementById('topbar')!;
const messagesEl = document.getElementById('messages')!;
const composerEl = document.getElementById('composer')!;
const promptEl = document.getElementById('prompt') as HTMLTextAreaElement;
const toolbarEl = document.getElementById('toolbar')!;

// ── Local state (rebuilt from hydrate) ───────────────────────────────────────
let busy = false;
let agentMode = true;
let currentModel = '';
let streamEl: HTMLElement | null = null; // live assistant bubble while tokens stream
let streamRaw = '';
// The act's narration an approval card sealed (the card comes after it): dropped with the stream at the act's reset.
let actNarration: HTMLElement[] = [];
let transcriptEmpty = true;
let slashCommands: WvSlashCommand[] = [];
let toolBubblesExpanded = false;
let contextWindow = 0;

// ── Header: model and server · new conversation · conversations · more ──────
// One button names the model and the server it runs on, with the connection's dot; it opens the model list.
let models: string[] = [];
let connection: WvBackendStatus | null = null;
let planMode = false;
let editorFile: string | null = null;
let problems = 0;
/** The line naming a question's attachments, as the extension writes it ("📎 Attached: {0}"). */
let attachedRecap = '';

const modelBtn = document.createElement('button');
modelBtn.id = 'modelbtn';
const modelDot = document.createElement('span');
modelDot.className = 'dot';
const modelName = document.createElement('span');
modelName.className = 'mname';
const modelMeta = document.createElement('span');
modelMeta.className = 'mmeta';
modelBtn.append(modelDot, modelName, modelMeta, icon('chevronDown', 12));
const modelPop = document.createElement('div');
modelPop.id = 'modelpop';
modelPop.className = 'menu';
modelPop.hidden = true;

const newBtn = iconButton('plus', t('chatNewConversation'), 15);
newBtn.addEventListener('click', () => post({ type: 'reset' }));
const historyBtn = iconButton('history', t('chatConversations'), 15);
historyBtn.addEventListener('click', () => post({ type: 'openSessions' }));
const moreBtn = iconButton('more', t('chatMore'), 15);
const moreMenu = document.createElement('div');
moreMenu.id = 'moremenu';
moreMenu.className = 'menu';
moreMenu.hidden = true;
topbarEl.append(modelBtn, newBtn, historyBtn, moreBtn);
document.body.append(modelPop, moreMenu);

function placeMenu(menu: HTMLElement, anchor: HTMLElement, alignRight: boolean): void {
  const r = anchor.getBoundingClientRect();
  menu.style.top = `${r.bottom + 4}px`;
  if (alignRight) {
    menu.style.left = '';
    menu.style.right = `${Math.max(4, document.documentElement.clientWidth - r.right)}px`;
  } else {
    menu.style.right = '';
    menu.style.left = `${r.left}px`;
    menu.style.minWidth = `${r.width}px`;
  }
}

function menuItem(label: string, onPick: () => void, checked = false): HTMLElement {
  const item = document.createElement('div');
  item.className = 'menu-item' + (checked ? ' checked' : '');
  item.setAttribute('role', 'menuitem');
  item.tabIndex = -1;
  const mark = document.createElement('span');
  mark.className = 'menu-mark';
  if (checked) {
    mark.appendChild(icon('check', 12));
  }
  const text = document.createElement('span');
  text.textContent = label;
  item.append(mark, text);
  item.addEventListener('mousedown', (e) => {
    e.preventDefault();
    closeMenus();
    onPick();
  });
  return item;
}

function closeMenus(): void {
  modelPop.hidden = true;
  moreMenu.hidden = true;
  plusMenu.hidden = true;
}

function renderModelMenu(): void {
  modelPop.textContent = '';
  if (connection && !connection.connected) {
    modelPop.appendChild(menuItem(t('retry'), () => post({ type: 'retryConnection' })));
  }
  if (models.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'menu-note';
    empty.textContent = t('noModelListed');
    modelPop.appendChild(empty);
  }
  for (const name of models) {
    modelPop.appendChild(menuItem(name, () => {
      currentModel = name;
      renderModelButton();
      renderWelcomeLine();
      post({ type: 'pickModel', model: name });
    }, name === currentModel));
  }
}

function openModelMenu(): void {
  renderModelMenu();
  placeMenu(modelPop, modelBtn, false);
  modelPop.hidden = false;
  // ⚠ The list the view holds is drawn at once, and the server is asked again, as Visual Studio does at every opening:
  // read only at start-up, a model pulled or deleted with /models — or from a terminal — reached this menu at the next
  // reload, and after a first /models pull the menu said "no model listed" under the model in use.
  post({ type: 'listModels' });
}

modelBtn.addEventListener('click', () => {
  const open = modelPop.hidden;
  closeMenus();
  if (open) {
    openModelMenu();
  }
});

moreBtn.addEventListener('click', () => {
  const open = moreMenu.hidden;
  closeMenus();
  if (!open) {
    return;
  }
  moreMenu.textContent = '';
  moreMenu.append(
    menuItem(t('chatMenuSearch'), () => toggleSearch(true)),
    menuItem(t('chatMenuExport'), () => post({ type: 'menu', action: 'export' })),
    menuItem(t('chatMenuXray'), () => post({ type: 'openXray' })),
    menuItem(t('chatMenuSettings'), () => post({ type: 'menu', action: 'settings' })),
  );
  placeMenu(moreMenu, moreBtn, true);
  moreMenu.hidden = false;
});

document.addEventListener('mousedown', (e) => {
  const target = e.target as Node;
  if (![modelBtn, moreBtn, plusBtn, modelPop, moreMenu, plusMenu].some((el) => el.contains(target))) {
    closeMenus();
  }
});
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeMenus();
  }
});

/** The button: dot, model, then server and the models in graphics memory. */
function renderModelButton(): void {
  const status = connection;
  modelDot.className = 'dot ' + (status === null ? '' : status.connected ? 'ok' : status.refused ? 'warn' : 'ko');
  modelName.textContent = currentModel || '—';
  // A server that refused the check (a wrong API key) is running: the host's sentence names it.
  const state = status && !status.connected ? (status.refused ?? t('statusUnreachable')) : '';
  const parts = [status?.server ?? '', state, status?.connected && status.vramBadge ? 'VRAM ' + status.vramBadge : '']
    .filter((p) => p);
  modelMeta.textContent = parts.join(' · ');
  modelBtn.title = t('chatModelButton', [currentModel, ...parts].filter((p) => p).join(' · '));
  modelBtn.setAttribute('aria-label', modelBtn.title);
}

function setBackendStatus(status: WvBackendStatus | null): void {
  connection = status;
  renderModelButton();
  renderWelcomeLine();
}

// Search bar: dims the turns that do not match (VS parity: dimmed, not hidden).
const searchBar = document.createElement('div');
searchBar.id = 'searchbar';
searchBar.hidden = true;
const searchInput = document.createElement('input');
searchInput.id = 'searchInput';
searchInput.placeholder = t('searchPlaceholder');
const searchClear = document.createElement('button');
setIcon(searchClear, 'close', undefined, 14);
searchClear.title = t('close');
searchClear.setAttribute('aria-label', t('close'));
searchBar.append(searchInput, searchClear);
topbarEl.insertAdjacentElement('afterend', searchBar);

function applySearch(): void {
  const q = searchInput.value.trim().toLowerCase();
  for (const el of messagesEl.querySelectorAll<HTMLElement>('.user-row, .turn')) {
    el.classList.toggle('search-dim', q.length > 0 && !(el.textContent ?? '').toLowerCase().includes(q));
  }
}
function toggleSearch(open: boolean): void {
  searchBar.hidden = !open;
  if (open) {
    searchInput.focus();
  } else {
    searchInput.value = '';
    applySearch();
  }
}
searchClear.addEventListener('click', () => toggleSearch(false));
searchInput.addEventListener('input', applySearch);

// ── Composer: chips · prompt · attach · mode · context ring · send ──────────
const plusBtn = iconButton('attach', t('chatAttach'), 15);
plusBtn.id = 'plus';
const plusMenu = document.createElement('div');
plusMenu.id = 'plusmenu';
plusMenu.className = 'menu';
plusMenu.hidden = true;
document.body.appendChild(plusMenu);
plusBtn.addEventListener('click', () => {
  const open = plusMenu.hidden;
  closeMenus();
  if (!open) {
    return;
  }
  plusMenu.textContent = '';
  plusMenu.append(
    menuItem(t('attachActiveFile'), () => post({ type: 'attachActive' })),
    menuItem(t('attachSelection'), () => post({ type: 'attachSelection' })),
    menuItem(t('attachBrowse'), () => post({ type: 'attachBrowse' })),
    menuItem(t('pinActiveFile'), () => post({ type: 'pinActive' })),
  );
  // The composer is at the bottom: the menu opens upwards, over the conversation.
  const r = plusBtn.getBoundingClientRect();
  plusMenu.style.left = `${r.left}px`;
  plusMenu.style.right = '';
  plusMenu.style.top = '';
  plusMenu.style.bottom = `${document.documentElement.clientHeight - r.top + 4}px`;
  plusMenu.hidden = false;
});

/** Chat, Agent or Plan: one switch for what the turn may do. */
const modeGroup = document.createElement('div');
modeGroup.id = 'modes';
modeGroup.setAttribute('role', 'radiogroup');
modeGroup.setAttribute('aria-label', t('modeAgent') + ' / ' + t('modeChat') + ' / ' + t('modePlan'));
const modeButtons = new Map<'chat' | 'agent' | 'plan', HTMLButtonElement>();
for (const [mode, label, tip] of [
  ['chat', t('modeChat'), t('modeChatTip')],
  ['agent', t('modeAgent'), t('modeAgentTip')],
  ['plan', t('modePlan'), t('modePlanTip')],
] as const) {
  const b = document.createElement('button');
  b.setAttribute('role', 'radio');
  b.textContent = label;
  b.title = tip;
  b.addEventListener('click', () => {
    applyModeTo(mode);
    post({ type: 'setMode', mode });
  });
  modeButtons.set(mode, b);
  modeGroup.appendChild(b);
}

function currentMode(): 'chat' | 'agent' | 'plan' {
  return planMode ? 'plan' : agentMode ? 'agent' : 'chat';
}

function applyModeTo(mode: 'chat' | 'agent' | 'plan'): void {
  for (const [m, b] of modeButtons) {
    b.setAttribute('aria-checked', String(m === mode));
    b.classList.toggle('on', m === mode);
  }
}

function applyAgentMode(enabled: boolean): void {
  agentMode = enabled;
  applyModeTo(currentMode());
}

function applyPlanMode(enabled: boolean): void {
  planMode = enabled;
  applyModeTo(currentMode());
}

/** How full the next question's window is, as a ring; it opens the X-Ray. */
const ring = document.createElement('button');
ring.id = 'ring';
ring.hidden = true;
const SVG = 'http://www.w3.org/2000/svg';
const ringSvg = document.createElementNS(SVG, 'svg');
ringSvg.setAttribute('viewBox', '0 0 20 20');
ringSvg.setAttribute('width', '16');
ringSvg.setAttribute('height', '16');
ringSvg.setAttribute('aria-hidden', 'true');
const ringTrack = document.createElementNS(SVG, 'circle');
const ringFill = document.createElementNS(SVG, 'circle');
for (const c of [ringTrack, ringFill]) {
  c.setAttribute('cx', '10');
  c.setAttribute('cy', '10');
  c.setAttribute('r', '8');
  c.setAttribute('fill', 'none');
  c.setAttribute('stroke-width', '3');
}
ringTrack.setAttribute('class', 'ring-track');
ringFill.setAttribute('class', 'ring-fill');
ringFill.setAttribute('transform', 'rotate(-90 10 10)');
ringSvg.append(ringTrack, ringFill);
const ringText = document.createElement('span');
ring.append(ringSvg, ringText);
ring.addEventListener('click', () => post({ type: 'openXray' }));

const sendBtn = document.createElement('button');
sendBtn.id = 'send';
sendBtn.addEventListener('click', () => {
  if (busy) {
    post({ type: 'cancel' });
  } else {
    send();
  }
});
const toolSpacer = document.createElement('span');
toolSpacer.className = 'spacer';
toolbarEl.append(plusBtn, modeGroup, toolSpacer, ring, sendBtn);

function setBusy(value: boolean): void {
  busy = value;
  setIcon(sendBtn, value ? 'stop' : 'send', undefined, 15);
  sendBtn.title = value ? t('cancelTitle') : t('sendTitle');
  sendBtn.setAttribute('aria-label', sendBtn.title);
  sendBtn.classList.toggle('stop', value);
}

/** Same thresholds as the Visual Studio gauge (50/80/95 %), the theme's chart colours. */
function updateGauge(promptTokens: number, lastTokens: number): void {
  if (contextWindow <= 0 || promptTokens <= 0) {
    ring.hidden = true;
    return;
  }
  // ⚠ The clamp belongs to the ARC, never to the value printed: past 100 % the backend is already dropping the head of
  // the conversation, and "100%" read as "full" where the Visual Studio gauge says 187 % (ContextBudgetGauge.Compute).
  const pct = (promptTokens * 100) / contextWindow;
  const arc = Math.min(100, pct);
  const circumference = 2 * Math.PI * 8;
  ringFill.setAttribute('stroke-dasharray', `${(circumference * arc) / 100} ${circumference}`);
  ringFill.style.stroke = pct < 50 ? 'var(--vscode-descriptionForeground)'
    : pct < 80 ? 'var(--vscode-charts-yellow)'
    : pct < 95 ? 'var(--vscode-charts-orange)'
    : 'var(--vscode-charts-red)';
  ringText.textContent = `${pct.toFixed(0)}%`;
  ring.title = t('contextRingTip', pct.toFixed(0), contextWindow.toLocaleString())
    + (lastTokens > 0 ? ' · ' + t('tokensInfo', lastTokens.toLocaleString()) : '');
  ring.setAttribute('aria-label', ring.title);
  ring.hidden = false;
}

// ── Turns: the question on the right, the answer below it — steps, text, result ──
// Follows the stream only while the user is at the bottom, like the Visual Studio window
// (ChatAutoScroller, same 50 px): scrolling up to reread stops the follow, coming back resumes it.
const FOLLOW_THRESHOLD_PX = 50;
let following = true;
messagesEl.addEventListener('scroll', () => {
  following = messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight <= FOLLOW_THRESHOLD_PX;
});

function scrollToBottom(): void {
  if (following) {
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }
}

interface TurnView {
  el: HTMLElement;
  who: HTMLElement;
  when: HTMLElement;
  plan: HTMLElement;
  run: HTMLElement | null;
  runGlyph: HTMLElement | null;
  runTitle: HTMLElement | null;
  runDetail: HTMLElement | null;
  runList: HTMLElement | null;
  steps: number;
  body: HTMLElement;
}

/** The answer being written; null between a question and its first output. */
let turn: TurnView | null = null;

function newTurn(model: string): TurnView {
  hideWelcome();
  const el = document.createElement('section');
  el.className = 'turn';
  const head = document.createElement('div');
  head.className = 'turn-head';
  const who = document.createElement('span');
  who.className = 'who';
  who.textContent = model;
  const when = document.createElement('span');
  when.className = 'when';
  head.append(who, when);
  const plan = document.createElement('div');
  plan.className = 'turn-plan';
  plan.hidden = true;
  const body = document.createElement('div');
  body.className = 'turn-body';
  el.append(head, plan, body);
  messagesEl.appendChild(el);
  turn = { el, who, when, plan, run: null, runGlyph: null, runTitle: null, runDetail: null, runList: null, steps: 0, body };
  return turn;
}

function ensureTurn(): TurnView {
  return turn ?? newTurn('');
}

function setStatus(text: string): void {
  if (turn && busy) {
    turn.when.textContent = text;
  }
}

function renderPlan(plan: WvPlan | null): void {
  const target = turn?.plan;
  if (!target) {
    return;
  }
  target.textContent = '';
  target.hidden = !plan;
  if (!plan) {
    return;
  }
  const goal = document.createElement('div');
  goal.className = 'plan-goal';
  setIcon(goal, 'goal', plan.goal, 13);
  target.appendChild(goal);
  for (const step of plan.steps) {
    const row = document.createElement('div');
    const s = step.status.toLowerCase();
    const state = s.includes('done') || s.includes('completed') ? 'done'
      : s.includes('running') || s.includes('progress') || s.includes('active') ? 'running'
      : s.includes('fail') || s.includes('error') || s.includes('skip') ? 'failed'
      : 'pending';
    const glyph: IconName = state === 'done' ? 'check' : state === 'running' ? 'running' : state === 'failed' ? 'failed' : 'pending';
    row.className = 'plan-step ' + state;
    setIcon(row, glyph, step.text, 13);
    target.appendChild(row);
  }
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
  setIcon(copy, 'copy', undefined, 13);
  copy.title = t('copy');
  copy.setAttribute('aria-label', t('copy'));
  copy.addEventListener('click', () => post({ type: 'copyText', text: copyText ?? item.text }));
  meta.appendChild(copy);
  return meta;
}

/** The attachments named under a question ("📎 Attached: a · b"): drawn as chips, the text kept without them. */
function splitAttachments(text: string): { text: string; names: string[] } {
  const prefix = attachedRecap.split('{0}')[0];
  if (!prefix) {
    return { text, names: [] };
  }
  const at = text.lastIndexOf(prefix);
  if (at < 0 || text.slice(at).includes('\n')) {
    return { text, names: [] };
  }
  return { text: text.slice(0, at).trimEnd(), names: text.slice(at + prefix.length).split(' · ').filter((n) => n) };
}

function addUser(item: WvTranscriptItem): void {
  hideWelcome();
  turn = null;
  const row = document.createElement('div');
  row.className = 'user-row';
  const { text, names } = splitAttachments(item.text);
  const bubble = document.createElement('div');
  bubble.className = 'bubble user';
  const body = document.createElement('div');
  body.className = 'bubble-body';
  renderMarkdownInto(body, text, false);
  // A question is shown and copied whole (it may quote a reasoning tag); only an answer's hidden reasoning stays out.
  bubble.append(body, metaRow(item, item.role === 'assistant' ? stripThinkTags(item.text) : undefined));
  row.appendChild(bubble);
  if (names.length > 0) {
    const chips = document.createElement('div');
    chips.className = 'user-chips';
    for (const name of names) {
      const chip = document.createElement('span');
      chip.className = 'chip';
      setIcon(chip, 'file', name, 11);
      chips.appendChild(chip);
    }
    row.appendChild(chips);
  }
  messagesEl.appendChild(row);
  scrollToBottom();
}

/** An answer, a notice or an error, in the current turn. */
function addAnswer(role: string, item: WvTranscriptItem): HTMLElement {
  const target = ensureTurn();
  const el = document.createElement('div');
  el.className = 'bubble ' + role + (item.notice ? ' notice' : '');
  const body = document.createElement('div');
  body.className = 'bubble-body';
  renderMarkdownInto(body, item.text);
  el.appendChild(body);
  // Copy what the answer shows: it keeps the model's inline reasoning in its text. A question is copied whole.
  el.appendChild(metaRow(item, role === 'assistant' ? stripThinkTags(item.text) : undefined));
  target.body.appendChild(el);
  scrollToBottom();
  return el;
}

function addBubble(role: string, item: WvTranscriptItem): HTMLElement | null {
  if (role === 'user') {
    addUser(item);
    return null;
  }
  return addAnswer(role, item);
}

/** The "N steps · …" line of a turn, folded by default; a failed step opens it. */
function ensureRun(target: TurnView): HTMLElement {
  if (target.runList) {
    return target.runList;
  }
  const run = document.createElement('div');
  run.className = 'run';
  const head = document.createElement('button');
  head.className = 'run-head';
  const chevron = document.createElement('span');
  chevron.className = 'run-chevron';
  // A tool while the run works; a check, or a cross, once it has ended (renderRunSummary).
  const glyph = document.createElement('span');
  glyph.className = 'run-glyph';
  setIcon(glyph, 'tool', undefined, 13);
  const title = document.createElement('span');
  title.className = 'run-title';
  const detail = document.createElement('span');
  detail.className = 'run-detail';
  head.append(glyph, title, detail, chevron);
  const list = document.createElement('div');
  list.className = 'run-list';
  let open = busy || toolBubblesExpanded;
  const apply = (): void => {
    list.hidden = !open;
    head.setAttribute('aria-expanded', String(open));
    setIcon(chevron, open ? 'chevronDown' : 'chevronRight', undefined, 12);
  };
  head.addEventListener('click', () => {
    open = !open;
    apply();
  });
  apply();
  run.append(head, list);
  target.el.insertBefore(run, target.body);
  target.run = run;
  target.runGlyph = glyph;
  target.runTitle = title;
  target.runDetail = detail;
  target.runList = list;
  (run as HTMLElement & { openRun?: () => void }).openRun = () => {
    open = true;
    apply();
  };
  (run as HTMLElement & { foldRun?: () => void }).foldRun = () => {
    open = false;
    apply();
  };
  return list;
}

/** What a step acted on: the path, command, query or address its arguments name. */
function stepSubject(input: string | undefined): string {
  if (!input) {
    return '';
  }
  try {
    const args = JSON.parse(input) as Record<string, unknown>;
    for (const key of ['path', 'file_path', 'command', 'query', 'url', 'symbol', 'pattern']) {
      const value = args[key];
      if (typeof value === 'string' && value) {
        return value.length > 80 ? value.slice(0, 80) + '…' : value;
      }
    }
  } catch {
    // Not JSON (a custom tool's raw arguments): the name alone says enough.
  }
  return '';
}

/** One tool call: a line of the run, its arguments and output folded under it. */
function addToolBubble(item: WvTranscriptItem, expanded: boolean): void {
  const target = ensureTurn();
  const list = ensureRun(target);
  target.steps++;
  if (target.runTitle) {
    target.runTitle.textContent = target.steps === 1 ? t('stepsOne') : t('stepsMany', target.steps);
  }
  if (busy) {
    target.when.textContent = t('turnWorking', target.steps + 1);
  }

  const step = document.createElement('div');
  step.className = 'step' + (item.hasErrors ? ' failed' : '');
  const line = document.createElement('button');
  line.className = 'step-line';
  const name = document.createElement('code');
  name.textContent = item.text;
  const subject = document.createElement('span');
  subject.className = 'step-subject';
  subject.textContent = stepSubject(item.toolInput);
  line.append(icon(item.hasErrors ? 'failed' : 'check', 13), name, subject);
  step.appendChild(line);

  if (item.hasErrors) {
    // "Fix with AI": pre-fills the prompt with the failing output (VS parity).
    const fix = document.createElement('button');
    fix.className = 'bubble-action';
    setIcon(fix, 'tool', t('fixWithAi'), 13);
    fix.addEventListener('click', () => {
      promptEl.value = t('fixPrompt') + '\n\n```\n' + (item.toolOutput ?? '') + '\n```';
      promptEl.focus();
    });
    step.appendChild(fix);
  }

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
  // Errors always start expanded — the red output is the point of the step.
  let open = expanded || item.hasErrors === true;
  body.hidden = !open;
  line.addEventListener('click', () => {
    open = !open;
    body.hidden = !open;
  });
  step.appendChild(body);
  list.appendChild(step);
  if (item.hasErrors) {
    (target.run as HTMLElement & { openRun?: () => void }).openRun?.();
  }
  scrollToBottom();
}

/** The run's line and result bar, once the turn has ended: what it did, the files it changed, the last check. */
function renderRunSummary(target: TurnView, run: RunSummary | null | undefined): void {
  if (!run) {
    return;
  }
  ensureRun(target);
  if (target.runTitle) {
    target.runTitle.textContent = run.title;
  }
  if (target.runDetail) {
    target.runDetail.textContent = run.detail;
  }
  const stepFailed = target.runList?.querySelector('.step.failed') != null;
  // Its steps showed while it worked; ended, the run folds into its line — unless the user keeps tool calls open,
  // or a step failed (its red output is the point).
  if (!toolBubblesExpanded && !stepFailed) {
    (target.run as HTMLElement & { foldRun?: () => void }).foldRun?.();
  }
  if (target.runGlyph) {
    // Failed: its last check failed, or, with no check, one of its steps did.
    const failed = run.check === 'buildFailed' || run.check === 'testsFailed' || (run.check === 'none' && stepFailed);
    setIcon(target.runGlyph, failed ? 'failed' : 'check', undefined, 13);
    target.runGlyph.classList.toggle('ok', !failed);
    target.runGlyph.classList.toggle('failed', failed);
  }
  if (run.files.length === 0 && run.check === 'none') {
    return;
  }
  const bar = document.createElement('div');
  bar.className = 'result';
  for (const f of run.files) {
    const file = document.createElement('button');
    file.className = 'result-file';
    file.title = f.path;
    file.append(icon('file', 13));
    const label = document.createElement('span');
    label.textContent = f.name;
    const plus = document.createElement('span');
    plus.className = 'plus';
    plus.textContent = `+${f.added}`;
    const minus = document.createElement('span');
    minus.className = 'minus';
    minus.textContent = `−${f.removed}`;
    file.append(label, plus, minus);
    if (!f.gone) {
      file.addEventListener('click', () => post({ type: 'openFile', path: f.path }));
    }
    bar.appendChild(file);
  }
  if (run.check !== 'none') {
    const check = document.createElement('span');
    const failed = run.check === 'buildFailed' || run.check === 'testsFailed';
    check.className = 'result-check' + (failed ? ' failed' : '');
    setIcon(check, failed ? 'failed' : 'check', run.checkText, 13);
    bar.appendChild(check);
  }
  if (run.runId) {
    // Undo reverts the most recent run that changed files (/undo-run): only that run's bar offers it.
    for (const old of messagesEl.querySelectorAll('.result .undo')) {
      old.remove();
    }
    const undo = document.createElement('button');
    undo.className = 'undo';
    setIcon(undo, 'regenerate', t('runUndo'), 13);
    undo.addEventListener('click', () => post({ type: 'send', text: '/undo-run' }));
    bar.appendChild(undo);
  }
  target.el.appendChild(bar);
}

function ensureStreamBubble(): HTMLElement {
  if (!streamEl) {
    const target = ensureTurn();
    streamEl = document.createElement('div');
    streamEl.className = 'bubble assistant streaming';
    const body = document.createElement('div');
    body.className = 'bubble-body';
    streamEl.appendChild(body);
    target.body.appendChild(streamEl);
    streamRaw = '';
  }
  return streamEl;
}

let streamRenderPending = false;
/** The mention query the popup is currently waiting on — older answers are dropped. */
let latestMentionQuery = '';
let mentionQueryTimer: ReturnType<typeof setTimeout> | undefined;
// Same rule for the @file / @folder sub-search: requests run side by side, and an older, slower one
// must not overwrite the answer to the query now typed — nor land in another category's popup.
let latestMentionSearch = { category: '', query: '' };

/**
 * Renders the streaming answer at most once per frame. Re-parsing the whole Markdown for every token
 * made a long answer cost quadratic work — thousands of full renders for a single reply.
 */
function scheduleStreamRender(): void {
  if (streamRenderPending) {
    return;
  }
  streamRenderPending = true;
  requestAnimationFrame(() => {
    streamRenderPending = false;
    if (!streamEl) {
      return; // the turn ended (final text rendered) or the stream was reset meanwhile
    }
    renderMarkdownInto(streamEl.querySelector('.bubble-body') as HTMLElement, streamRaw);
    scrollToBottom();
  });
}

function finishStream(): void {
  if (streamEl) {
    streamEl.classList.remove('streaming');
    streamEl = null;
    streamRaw = '';
  }
}

/** Offers a regenerate button under the newest answer only. */
function refreshRegenerate(): void {
  for (const old of messagesEl.querySelectorAll('.bubble-regen')) {
    old.remove();
  }
  const answers = messagesEl.querySelectorAll<HTMLElement>('.bubble.assistant');
  const last = answers.length > 0 ? answers[answers.length - 1] : null;
  if (!last || busy) {
    return;
  }
  const btn = document.createElement('button');
  btn.className = 'bubble-action bubble-regen';
  setIcon(btn, 'regenerate', t('regenerate'), 13);
  btn.addEventListener('click', () => post({ type: 'regenerate' }));
  last.querySelector('.bubble-meta')?.appendChild(btn);
}

// ── Approval card: what will happen, to what, the start of the change — Allow once, Always, Deny ──
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

// Cards still awaiting an answer, by id — so a host-side cancellation can retire its card.
const approvalCards = new Map<number, HTMLElement>();

function kbd(text: string): HTMLElement {
  const k = document.createElement('kbd');
  k.textContent = text;
  return k;
}

function addApprovalCard(id: number, message: string, card?: ApprovalCard | null): void {
  if (streamEl) {
    actNarration.push(streamEl);
  }
  finishStream();
  const target = ensureTurn();
  target.when.textContent = t('turnWaiting');
  const el = document.createElement('div');
  el.className = 'approval';
  el.setAttribute('role', 'group');
  approvalCards.set(id, el);

  const head = document.createElement('div');
  head.className = 'approval-head';
  const title = document.createElement('span');
  title.className = 'approval-title';
  title.textContent = card?.title ?? '';
  const subject = document.createElement('span');
  subject.className = 'approval-subject';
  subject.textContent = [card?.subject, card?.meta].filter((p) => p).join(' · ');
  const openDiff = document.createElement('button');
  openDiff.className = 'linkbtn';
  openDiff.textContent = t('approvalOpenDiff');
  openDiff.addEventListener('click', () => post({ type: 'openApprovalDiff', text: card?.message ?? message }));
  head.append(icon('edit', 14), title, subject, openDiff);
  el.setAttribute('aria-label', [card?.title, card?.subject].filter((p) => p).join(' — ') || message);

  let preview: HTMLElement;
  if (card && card.preview.length > 0) {
    preview = document.createElement('pre');
    preview.className = 'approval-preview';
    for (const line of card.preview) {
      const span = document.createElement('span');
      span.className = 'pl ' + line.kind;
      span.textContent = (line.kind === 'add' ? '+ ' : line.kind === 'del' ? '- ' : '  ') + line.text;
      preview.appendChild(span);
    }
    if (card.more) {
      const more = document.createElement('span');
      more.className = 'pl gap';
      more.textContent = '  ' + card.more;
      preview.appendChild(more);
    }
  } else {
    preview = renderApprovalMessage(card ? card.message : message);
  }

  const actions = document.createElement('div');
  actions.className = 'approval-actions';
  const answer = (value: number, chosen: HTMLButtonElement): void => {
    if (!approvalCards.has(id)) {
      return;
    }
    approvalCards.delete(id);
    post({ type: 'approvalAnswer', id, answer: value });
    el.classList.add('answered');
    for (const b of actions.querySelectorAll('button')) {
      (b as HTMLButtonElement).disabled = true;
    }
    chosen.classList.add('chosen');
    if (busy && turn) {
      turn.when.textContent = t('turnWorking', turn.steps + 1);
    }
  };
  const allow = document.createElement('button');
  allow.className = 'primary';
  allow.append(document.createTextNode(t('allowOnce')), kbd('Enter'));
  allow.addEventListener('click', () => answer(1, allow));
  const always = document.createElement('button');
  always.textContent = t('allowAlways');
  always.title = card?.alwaysTooltip ?? '';
  always.addEventListener('click', () => answer(2, always));
  const deny = document.createElement('button');
  deny.className = 'deny';
  deny.append(document.createTextNode(t('deny')), kbd('Esc'));
  deny.addEventListener('click', () => answer(0, deny));
  actions.append(allow, always, deny);
  el.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      answer(0, deny);
    }
  });

  el.append(card ? head : document.createElement('span'), preview, actions);
  // In the answer flow, in arrival order: the turn's body exists from its start, so a card put after it would
  // stand BELOW the answer that follows it — the answer then reads above the approvals it depends on, out of view.
  target.body.appendChild(el);
  // ⚠ The card never takes the keyboard: a key typed for the composer or the code — Enter, a space — would land on
  // "Allow once" and approve a tool nobody read. Enter and Esc in an EMPTY composer answer it (answerWaitingCard), as
  // in Visual Studio.
  scrollToBottom();
}

/** Answers the oldest card still waiting (1 allow once, 0 deny) through its own button; false when none waits. */
function answerWaitingCard(value: 0 | 1): boolean {
  const waiting = approvalCards.values().next().value as HTMLElement | undefined;
  const button = waiting?.querySelector<HTMLButtonElement>(
    value === 1 ? '.approval-actions button.primary' : '.approval-actions button.deny');
  if (!button || button.disabled) {
    return false;
  }
  button.click();
  return true;
}

// §27.5 — the run this card belonged to was cancelled host-side: freeze it (same inert look as
// an answered card) so its buttons cannot answer into a run that no longer exists.
function dismissApprovalCard(id: number): void {
  const card = approvalCards.get(id);
  approvalCards.delete(id);
  if (!card) {
    return; // already answered
  }
  card.classList.add('answered');
  for (const b of card.querySelectorAll('.approval-actions button')) {
    (b as HTMLButtonElement).disabled = true;
  }
}

// ── Welcome: what to work on — the open file's actions, the errors, the keys ──
const welcomeEl = document.createElement('div');
welcomeEl.id = 'welcome';
welcomeEl.hidden = true;
messagesEl.appendChild(welcomeEl);
const welcomeLine = document.createElement('p');
welcomeLine.className = 'welcome-line';

function renderWelcomeLine(): void {
  welcomeLine.textContent = currentModel && connection?.server
    ? t('welcomeLine', currentModel, connection.server)
    : t('welcomeLocal');
}

function welcomeCard(glyph: IconName, title: string, desc: string, onPick: () => void): HTMLElement {
  const card = document.createElement('button');
  card.className = 'welcome-card';
  const box = document.createElement('span');
  box.className = 'welcome-icon';
  box.appendChild(icon(glyph, 16));
  const text = document.createElement('span');
  text.className = 'welcome-text';
  const t1 = document.createElement('span');
  t1.className = 'welcome-card-title';
  t1.textContent = title;
  text.appendChild(t1);
  if (desc) {
    const t2 = document.createElement('span');
    t2.className = 'welcome-card-desc';
    t2.textContent = desc;
    text.appendChild(t2);
  }
  card.append(box, text);
  card.addEventListener('click', onPick);
  return card;
}

function buildWelcome(): void {
  welcomeEl.textContent = '';
  const title = document.createElement('h1');
  title.className = 'welcome-title';
  title.textContent = t('welcomeTitle');
  renderWelcomeLine();
  welcomeEl.append(title, welcomeLine);

  const cards = document.createElement('div');
  cards.className = 'welcome-cards';
  if (editorFile) {
    const forFile = document.createElement('div');
    forFile.className = 'welcome-for';
    forFile.textContent = t('welcomeForFile', editorFile);
    const file = editorFile;
    cards.append(
      forFile,
      welcomeCard('explain', t('welcomeExplainFile'), t('welcomeExplainFileDesc'), () => post({ type: 'send', text: '/explain' })),
      welcomeCard('test', t('welcomeTestsFile'), t('welcomeTestsFileDesc'), () => post({ type: 'send', text: '/test' })),
      welcomeCard('search', t('welcomeUsagesFile'), t('welcomeUsagesFileDesc'),
        () => post({ type: 'send', text: t('welcomeUsagesPrompt', file) })),
    );
  } else {
    // Without a file, what works without one: /explain, /fix and /test would only answer "open a file".
    cards.append(
      welcomeCard('folder', t('cardProject'), '', () => post({ type: 'send', text: t('welcomeProjectPrompt') })),
      welcomeCard('history', t('cardChanges'), '', () => post({ type: 'send', text: t('welcomeChangesPrompt') })),
      welcomeCard('help', t('cardHelp'), '', () => post({ type: 'send', text: '/help' })),
    );
  }
  welcomeEl.appendChild(cards);
  if (!editorFile) {
    const openHint = document.createElement('div');
    openHint.className = 'welcome-for';
    openHint.textContent = t('welcomeOpenFileHint');
    welcomeEl.appendChild(openHint);
  }

  if (problems > 0) {
    const banner = document.createElement('div');
    banner.className = 'welcome-banner';
    const text = document.createElement('span');
    text.textContent = t('welcomeProblems', problems);
    const fix = document.createElement('button');
    fix.textContent = t('welcomeFixThem');
    // The Problems panel attached as the @problems picker attaches it, then the request: typed, "@problems" names no
    // file and reached the model as text, without a single error.
    fix.addEventListener('click', () => post({ type: 'fixProblems', text: t('welcomeFixPrompt') }));
    banner.append(icon('warning', 14), text, fix);
    welcomeEl.appendChild(banner);
  }

  const hints = document.createElement('div');
  hints.className = 'welcome-hints';
  const hint = (keys: string[], label: string): HTMLElement => {
    const span = document.createElement('span');
    for (const k of keys) {
      span.appendChild(kbd(k));
    }
    span.appendChild(document.createTextNode(' ' + label));
    return span;
  };
  hints.append(hint(['@'], t('welcomeHintAttach')), hint(['/'], t('welcomeHintCommands')),
    hint(['Shift', 'Enter'], t('welcomeHintNewLine')));
  welcomeEl.appendChild(hints);
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
        build: (row) => { setIcon(row, 'search', t('mentionSearchCode', query), 13); },
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
    latestMentionSearch = { category, query };
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
  // Free file suggestions under the categories, like the VS popup's open-file list. Debounced (each
  // query runs a workspace file search), and only the answer to the LATEST query is shown: a slow,
  // older search otherwise lands last and overwrites the right suggestions.
  const query = typing[1];
  latestMentionQuery = query;
  clearTimeout(mentionQueryTimer);
  mentionQueryTimer = setTimeout(() => post({ type: 'mentionQuery', query }), 120);
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
  // Quoted when the path holds a space: the extension reads a bare token up to the first space only.
  const written = mentionFor(path);
  promptEl.value = value.slice(0, mentionStart) + written + ' ' + value.slice(caret);
  const pos = mentionStart + written.length + 1;
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
        const before = value.slice(0, caret).replace(COMMITTED_RE, mentionFor(rel) + ' ');
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

/** Files pinned into every request, above the pending attachments — each can be unpinned here. */
const pinsEl = document.createElement('div');
pinsEl.id = 'pins';
pinsEl.hidden = true;
composerEl.insertBefore(pinsEl, chipsEl);

function renderPins(pins: string[]): void {
  pinsEl.textContent = '';
  pinsEl.hidden = pins.length === 0;
  for (const path of pins) {
    const el = document.createElement('span');
    el.className = 'chip pinned';
    el.title = path;
    const name = document.createElement('span');
    setIcon(name, 'pin', path.split(/[\\/]/).pop() ?? path, 12);
    const close = document.createElement('button');
    setIcon(close, 'close', undefined, 11);
    close.title = t('unpin');
    close.setAttribute('aria-label', t('unpin'));
    close.addEventListener('click', () => post({ type: 'unpin', path }));
    el.append(name, close);
    pinsEl.appendChild(el);
  }
}

function renderChips(chips: WvChip[]): void {
  chipsEl.textContent = '';
  chipsEl.hidden = chips.length === 0;
  chips.forEach((chip, i) => {
    const el = document.createElement('span');
    el.className = 'chip';
    const name = document.createElement('span');
    setIcon(name, 'file', chip.name, 12);
    const close = document.createElement('button');
    setIcon(close, 'close', undefined, 11);
    close.title = t('chipRemove');
    close.setAttribute('aria-label', t('chipRemove'));
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
  // ⚠ The list is re-read as a command starts, as Visual Studio does at every keystroke: read only at start-up, a
  // prompt file made by /prompts init (whose answer promises it in the autocomplete) or by hand never showed up.
  if (text === '/') {
    post({ type: 'listCommands' });
  }
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

function applySlash(command: string, key?: string): void {
  // Enter on an already fully-typed command sends it (VS parity) — only Tab (or a
  // partial prefix) completes to "/command " awaiting arguments.
  if (key === 'Enter' && promptEl.value.trim().toLowerCase() === command.toLowerCase()) {
    closeSlash();
    send();
    return;
  }
  promptEl.value = command + ' ';
  promptEl.setSelectionRange(promptEl.value.length, promptEl.value.length);
  promptEl.focus();
  closeSlash();
}

/** Shared ↑/↓/Tab/Enter/Escape navigation for a popup; true when the key was consumed. */
function popupKey(popup: Popup, e: KeyboardEvent, apply: (item: string, key?: string) => void, close: () => void): boolean {
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
    apply(popup.items[popup.index], e.key);
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

promptEl.placeholder = t('composerPlaceholder');
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
    // A card waiting and nothing typed: Enter is the "Allow once" the card names. With text, it never answers.
    if (promptEl.value.trim() === '' && answerWaitingCard(1)) {
      return;
    }
    send();
    return;
  }
  if (e.key === 'Escape' && promptEl.value.trim() === '' && answerWaitingCard(0)) {
    e.preventDefault();
  }
});

// ── State ← extension ────────────────────────────────────────────────────────
function renderTranscript(transcript: WvTranscriptItem[], running = false): void {
  // A rebuilt conversation (loaded session, branch, restored view) opens at its end, whatever the
  // previous one was scrolled to.
  following = true;
  messagesEl.textContent = '';
  messagesEl.appendChild(welcomeEl);
  streamEl = null;
  turn = null;
  transcriptEmpty = true;
  for (const item of transcript) {
    if (item.role === 'tool') {
      addToolBubble(item, toolBubblesExpanded);
    } else if (item.role === 'user' || item.role === 'assistant' || item.role === 'error') {
      addBubble(item.role, item);
      // An answer of this session keeps its model, duration and run; a saved one comes back without them.
      if (item.role === 'assistant' && turn) {
        const view: TurnView = turn;
        if (item.model) {
          view.who.textContent = item.model;
        }
        if (item.duration) {
          view.when.textContent = item.duration;
        }
        renderRunSummary(view, item.run);
      }
    }
  }
  // ⚠ A rebuild DURING a turn — the question renamed with the files it carries, a view restored — keeps the running
  // turn open, under the model's name. Closed, the plan, the status and the header of the answer being written had
  // nowhere to go: the plan card never appeared, and the next token opened a turn with no name.
  if (running) {
    const open = turn ?? newTurn(currentModel);
    if (!open.who.textContent) {
      open.who.textContent = currentModel;
    }
    open.when.textContent = t('turnWorking', open.steps + 1);
  } else {
    turn = null;
  }
  transcriptEmpty = transcript.length === 0 && !running;
  showWelcomeIfEmpty();
  refreshRegenerate();
  applySearch();
}

window.addEventListener('message', (event: MessageEvent<ExtToWebview>) => {
  const msg = event.data;
  switch (msg.type) {
    case 'commands': {
      slashCommands = msg.commands ?? slashCommands;
      detectSlash();
      break;
    }
    case 'models': {
      models = msg.models ?? models;
      if (!modelPop.hidden) {
        renderModelMenu();
        placeMenu(modelPop, modelBtn, false);
      }
      break;
    }
    case 'hydrate': {
      // Defensive defaults: survive a stale extension↔webview pair (in-place update).
      slashCommands = msg.commands ?? [];
      toolBubblesExpanded = msg.toolBubblesExpanded === true;
      document.body.classList.toggle('ip-compact', msg.compact === true);
      contextWindow = msg.contextWindow ?? 0;
      historyEntries = msg.history ?? [];
      currentModel = msg.model ?? '';
      models = msg.models ?? [];
      planMode = msg.planMode === true;
      editorFile = msg.editorFile ?? null;
      problems = msg.problems ?? 0;
      attachedRecap = msg.attachedRecap ?? '';
      mentionCategories = msg.mentionCategories ?? [];
      renderChips(msg.chips ?? []);
      renderPins(msg.pins ?? []);
      renderTranscript(msg.transcript ?? [], msg.busy === true);

      // The model list opens from the header; an empty list says the backend listed nothing (openModelMenu), never a
      // one-entry list that would read as a backend serving one model.
      applyAgentMode(msg.agentMode);
      setBackendStatus(msg.status);
      updateGauge(msg.promptTokens, msg.lastTokens);
      renderPlan(msg.plan);
      setBusy(msg.busy);
      renderModelButton();
      if (msg.busy && msg.stream) {
        const el = ensureStreamBubble();
        streamRaw = msg.stream;
        renderMarkdownInto(el.querySelector('.bubble-body') as HTMLElement, streamRaw);
      }
      break;
    }
    case 'turnStarted':
      actNarration = [];
      historyEntries = msg.history;
      // The question just sent must be visible, wherever the user had scrolled to.
      following = true;
      addBubble('user', { role: 'user', text: msg.prompt, timestamp: msg.timestamp });
      setBusy(true);
      newTurn(currentModel).when.textContent = t('turnWorking', 1);
      renderPlan(null);
      refreshRegenerate();
      break;
    case 'token': {
      ensureStreamBubble();
      streamRaw += msg.text;
      scheduleStreamRender();
      break;
    }
    case 'thinking':
      // The tail already carries its emoji; with no tail (plain chat), the generic indicator.
      setStatus(msg.text ? msg.text : t('thinking'));
      break;
    case 'status':
      setStatus(msg.text);
      break;
    case 'assistant':
      // Out-of-turn assistant bubble (e.g. a background /task finishing) — persistent, unlike a
      // status line the next setBusy wipes. Its own section, never inside the turn being written.
      if (!busy) {
        turn = null;
      }
      addBubble('assistant', { role: 'assistant', text: msg.text, timestamp: msg.timestamp });
      if (!busy) {
        turn = null;
      }
      break;
    case 'tool':
      // ⚠ The stream bubble stays open: the steps go to the run list ABOVE the answer area, so nothing needs it
      // sealed. Sealed here, the act's narration outlived the stream reset that drops it (and was gone on reload),
      // and an answer followed by a tool notice (the session recap) was drawn a second time at the end of the turn.
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
      addApprovalCard(msg.id, msg.message, msg.card);
      break;
    case 'approvalDismiss':
      dismissApprovalCard(msg.id);
      break;
    case 'mentionSuggestions':
      if (msg.query === latestMentionQuery) {
        renderMentions(msg.items);
      }
      break;
    case 'xrayPanel':
      renderXray(msg.panel);
      break;
    case 'streamReset':
      // The host drops what the act streamed — its narration is not the answer, and it is not in the saved thread.
      if (streamEl) {
        streamEl.remove();
        streamEl = null;
        streamRaw = '';
      }
      for (const sealed of actNarration) {
        sealed.remove();
      }
      actNarration = [];
      break;
    case 'backendStatus':
      setBackendStatus(msg.status);
      break;
    case 'agentMode':
      applyAgentMode(msg.enabled);
      break;
    case 'density':
      document.body.classList.toggle('ip-compact', msg.compact);
      break;
    case 'planMode':
      applyPlanMode(msg.enabled);
      break;
    case 'editorContext':
      editorFile = msg.file;
      problems = msg.problems;
      if (!welcomeEl.hidden) {
        buildWelcome();
      }
      break;
    case 'setPrompt':
      promptEl.value = msg.text;
      promptEl.focus();
      promptEl.setSelectionRange(promptEl.value.length, promptEl.value.length);
      break;
    case 'mentionResults':
      if (msg.query === latestMentionSearch.query && msg.category === latestMentionSearch.category) {
        renderMentionResults(msg.category, msg.items);
      }
      break;
    case 'chips':
      renderChips(msg.chips);
      break;
    case 'pins':
      renderPins(msg.pins);
      break;
    case 'stepPaused': {
      finishStream();
      hideWelcome();
      const pause = document.createElement('div');
      pause.className = 'bubble step-pause';
      const body = document.createElement('div');
      body.className = 'bubble-body';
      setIcon(body, 'pause', t('stepPaused'), 14);
      const resume = document.createElement('button');
      resume.className = 'bubble-action';
      setIcon(resume, 'play', t('resume'), 12);
      resume.addEventListener('click', () => post({ type: 'resumeStep' }));
      pause.append(body, resume);
      ensureTurn().body.appendChild(pause);
      scrollToBottom();
      break;
    }
    case 'stepResumed':
      for (const el of messagesEl.querySelectorAll('.step-pause')) {
        el.remove();
      }
      break;
    case 'turnEnded': {
      if (streamEl && msg.cancelled && !msg.text) {
        // Stopped before anything visible (reasoning only): no empty bubble, as in Visual Studio.
        streamEl.remove();
        finishStream();
      } else if (streamEl) {
        // Replace the stream bubble content with the authoritative final text.
        streamRaw = msg.text || streamRaw;
        renderMarkdownInto(streamEl.querySelector('.bubble-body') as HTMLElement, streamRaw);
        streamEl.appendChild(metaRow({ text: streamRaw, timestamp: msg.timestamp }, stripThinkTags(streamRaw)));
        finishStream();
      } else if (msg.text) {
        addBubble('assistant', { role: 'assistant', text: msg.text, timestamp: msg.timestamp });
      }
      // The line saying why the run stopped, AFTER the answer and without replacing it.
      if (msg.endNotice) {
        addBubble('assistant', { role: 'assistant', text: msg.endNotice, timestamp: msg.timestamp });
      }
      if (msg.error) {
        addBubble('error', { role: 'error', text: msg.error, timestamp: msg.timestamp });
      }
      if (msg.cancelled) {
        addBubble('error', { role: 'error', text: t('cancelled'), timestamp: msg.timestamp });
      }
      renderPlan(null);
      setBusy(false);
      // The answer's header: the model that answered and how long it took; under it, the run.
      if (turn) {
        const view: TurnView = turn;
        if (msg.model) {
          view.who.textContent = msg.model;
        }
        view.when.textContent = msg.duration ?? '';
        renderRunSummary(view, msg.run);
      }
      turn = null;
      if (typeof msg.contextWindow === 'number' && msg.contextWindow > 0) {
        contextWindow = msg.contextWindow;
      }
      updateGauge(msg.promptTokens, msg.tokens);
      refreshRegenerate();
      scrollToBottom();
      break;
    }
  }
});

post({ type: 'ready' });
