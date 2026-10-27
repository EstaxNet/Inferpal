// Inferpal chat webview. The extension host owns the transcript; this script only renders
// state pushed via postMessage and reports user intents back. It must survive being
// destroyed on hide: everything re-renders from the 'hydrate' message.
// No literal user-visible English here — strings go through t() (window.__l10n).
import type {
  ApprovalCard, CutCard, InputCard, InputCardField, InputFormAnswer, ModelWait, RewindCard, RunReview, RunReviewLine, RunSummary, SetupPlace, SetupPlan,
} from '../protocol';
import type {
  ExtToWebview,
  WebviewToExt,
  WvBackendStatus,
  WvBuildBanner,
  WvChip,
  WvImportCard,
  WvMentionCategory,
  WvMentionItem,
  WvPlan,
  WvSetupAction,
  WvSetupView,
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
// The banner of a build task that failed, over the conversation (Visual Studio's "Build Failed" banner).
const buildBannerEl = document.createElement('div');
buildBannerEl.id = 'build-banner';
buildBannerEl.hidden = true;
// Said by a screen reader when it appears or changes: a build fails while the person looks elsewhere.
buildBannerEl.setAttribute('role', 'status');
// What it shows now, and the failed build the person closed it for: a redraw already on its way never reopens it.
let buildBannerShown = '';
let buildBannerClosed = -1;
messagesEl.before(buildBannerEl);
const composerEl = document.getElementById('composer')!;
const promptEl = document.getElementById('prompt') as HTMLTextAreaElement;
const toolbarEl = document.getElementById('toolbar')!;

// ── Local state (rebuilt from hydrate) ───────────────────────────────────────
let busy = false;
/** The agent waits at a step pause: `/resume` typed in the composer resumes it. */
let stepPausedNow = false;
/** The extension's word on whether Regenerate has a question to ask again (hydrate, turnEnded). */
let canRegenerate = false;
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
  // A server that refused the check (a wrong API key) is running: the host's sentence names it. A host that is not
  // running asked nobody: its own sentence, never "unreachable".
  const state = status && !status.connected ? (status.hostDown ?? status.refused ?? t('statusUnreachable')) : '';
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

/** No end notice offers Continue any more: another turn starts, or the gesture was used. */
function forgetContinue(): void {
  for (const el of messagesEl.querySelectorAll('.continue-turn')) {
    el.remove();
  }
}

function setBusy(value: boolean): void {
  busy = value;
  if (value) {
    forgetContinue();
  } else {
    for (const el of messagesEl.querySelectorAll<HTMLElement>('.continue-turn')) {
      el.hidden = false;
    }
    // A wait never outlives its turn: stopped, failed or answered, the card goes with it.
    messagesEl.querySelectorAll('.wait-card').forEach((el) => el.remove());
  }
  setIcon(sendBtn, value ? 'stop' : 'send', undefined, 15);
  sendBtn.title = value ? t('cancelTitle') : t('sendTitle');
  sendBtn.setAttribute('aria-label', sendBtn.title);
  sendBtn.classList.toggle('stop', value);
  renderPlaceholder();
}

/** What the keys do in the EMPTY composer — the only one that shows this text: send; a note for the running turn, never
 *  a Stop; or, while one approval card waits, answer it (Enter allows once, Esc denies — answerWaitingCard). Two cards:
 *  no key answers either, so the turn's hint stays. */
function renderPlaceholder(): void {
  promptEl.placeholder = !busy ? t('composerPlaceholder')
    : approvalCards.size === 1 ? t('composerPlaceholderCard')
    : t('composerPlaceholderBusy');
}

// ── The queue: messages sent after the turn, in order, each removable ──────────
const queueEl = document.createElement('div');
queueEl.id = 'queue';
queueEl.hidden = true;
composerEl.insertBefore(queueEl, promptEl);

function renderQueue(items: readonly string[]): void {
  queueEl.textContent = '';
  queueEl.hidden = items.length === 0;
  if (items.length === 0) {
    return;
  }
  const title = document.createElement('div');
  title.className = 'queue-title';
  title.textContent = t('queueTitle');
  queueEl.appendChild(title);
  items.forEach((text, index) => {
    const row = document.createElement('div');
    row.className = 'queue-item';
    const label = document.createElement('span');
    label.className = 'queue-text';
    label.textContent = text;
    label.title = text;
    const remove = iconButton('close', t('queueRemove'), 12);
    remove.addEventListener('click', () => post({ type: 'unqueue', index }));
    row.append(label, remove);
    queueEl.appendChild(row);
  });
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

/** Under the cause of a turn cut at its bound: what it wrote, and the way to the time limits (the host's words). Asking
 *  again is the turn's own Regenerate. */
function renderCut(bubble: HTMLElement, cut: CutCard): void {
  const box = document.createElement('div');
  box.className = 'cut-card';
  if (cut.note) {
    const note = document.createElement('div');
    note.className = 'cut-note';
    note.textContent = cut.note;
    box.appendChild(note);
  }
  const open = document.createElement('button');
  open.className = 'cut-open';
  open.textContent = cut.openLimits;
  open.addEventListener('click', () => post({ type: 'openSettingsPage', page: cut.page, section: cut.section }));
  box.appendChild(open);
  (bubble.querySelector('.bubble-body') ?? bubble).appendChild(box);
}

/** The turn's wait card: what the server is doing, how long against what, and when it stops — gone with null. The
 *  words are the host's (`ModelWait`); the bar only when a read was measured. */
function renderWait(wait: ModelWait | null): void {
  let card = turn?.el.querySelector<HTMLElement>('.wait-card') ?? null;
  if (!wait || !turn || !busy) {
    card?.remove();
    return;
  }
  if (!card) {
    card = document.createElement('div');
    card.className = 'wait-card';
    // Only the title is announced: it changes once, to "longer than usual"; the times change every few seconds.
    card.setAttribute('role', 'group');
    const head = document.createElement('div');
    head.className = 'wait-head';
    const icon = document.createElement('span');
    icon.className = 'wait-icon';
    setIcon(icon, 'history', undefined, 15);
    const title = document.createElement('span');
    title.className = 'wait-title';
    title.setAttribute('aria-live', 'polite');
    const times = document.createElement('span');
    times.className = 'wait-times';
    head.append(icon, title, times);
    const bar = document.createElement('div');
    bar.className = 'wait-bar';
    bar.setAttribute('aria-hidden', 'true');
    const fill = document.createElement('div');
    fill.className = 'wait-fill';
    bar.appendChild(fill);
    const detail = document.createElement('div');
    detail.className = 'wait-detail';
    card.append(head, bar, detail);
    turn.body.appendChild(card);
    scrollToBottom();
  }
  card.classList.toggle('longer', wait.longer);
  card.querySelector<HTMLElement>('.wait-title')!.textContent = wait.title;
  card.querySelector<HTMLElement>('.wait-times')!.textContent = wait.times;
  card.querySelector<HTMLElement>('.wait-detail')!.textContent = wait.detail;
  const bar = card.querySelector<HTMLElement>('.wait-bar')!;
  bar.hidden = wait.fraction === null;
  card.querySelector<HTMLElement>('.wait-fill')!.style.width = `${Math.round((wait.fraction ?? 0) * 100)}%`;
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
  // A question the model was asked can be gone back to, with its files; a slash command's echo cannot.
  if (!item.notice) {
    const back = document.createElement('button');
    back.className = 'rewind-here';
    back.title = t('rewindHereTip');
    setIcon(back, 'regenerate', t('rewindHere'), 12);
    back.addEventListener('click', () => post({ type: 'rewindPlan', ordinal: questionOrdinal(row), question: item.text }));
    row.dataset.question = item.text;
    row.appendChild(back);
  }
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

/** The question a row shows, as saved: the extension and the host check it is still the one at that place. */
function questionOf(row: Element): string {
  return (row as HTMLElement).dataset.question ?? '';
}

/** The refused "Go back here" card that asked for a run's review: where its card goes when the run's bar is not on
 *  screen. */
const reviewAnchors = new Map<string, HTMLElement>();

/** The position of a question's row among the view's question rows — the extension's count too. */
function questionOrdinal(row: Element): number {
  return [...messagesEl.querySelectorAll('.user-row')].indexOf(row);
}

/**
 * A question's "Go back here" card, under it: the files going back would put back or delete, or why it cannot —
 * and then the conversation only, or the runs' review cards. `card` absent: the shown card takes its actions back.
 */
function renderRewindCard(ordinal: number, card: RewindCard | null | undefined, message?: string): void {
  const row = messagesEl.querySelectorAll('.user-row')[ordinal];
  if (!row) {
    return;
  }
  const old = row.nextElementSibling?.classList.contains('rewind-card') ? (row.nextElementSibling as HTMLElement) : null;
  if (card === undefined) {
    old?.querySelectorAll<HTMLButtonElement>('button').forEach((b) => (b.disabled = false));
    const said = old?.querySelector<HTMLElement>('.rewind-status');
    if (said && message) {
      said.textContent = message;
      said.hidden = false;
    }
    return;
  }
  old?.remove();
  if (!card) {
    return;
  }
  const el = document.createElement('section');
  el.className = 'approval rewind-card' + (card.refused ? ' refused' : '');
  el.setAttribute('role', card.refused ? 'alertdialog' : 'dialog');
  el.setAttribute('aria-label', card.title);
  const title = document.createElement('div');
  title.className = 'rewind-title';
  if (card.refused) {
    title.append(icon('warning', 14));
  }
  title.append(document.createTextNode(card.title));
  const body = document.createElement('p');
  body.className = 'rewind-body';
  body.textContent = card.body;
  el.append(title, body);
  const disableAll = () => el.querySelectorAll<HTMLButtonElement>('button').forEach((b) => (b.disabled = true));
  if (card.files.length > 0) {
    const list = document.createElement('ul');
    list.className = 'rewind-files';
    for (const f of card.files) {
      const li = document.createElement('li');
      li.className = f.kind;
      li.title = f.path;
      const name = document.createElement('span');
      name.className = 'rewind-name';
      name.textContent = f.name;
      const label = document.createElement('span');
      label.className = 'rewind-label';
      label.textContent = f.label;
      li.append(name, label);
      list.appendChild(li);
    }
    el.appendChild(list);
  }
  if (card.note) {
    const note = document.createElement('p');
    note.className = 'rewind-note';
    note.textContent = card.note;
    el.appendChild(note);
  }
  const status = document.createElement('div');
  status.className = 'rewind-status';
  status.setAttribute('role', 'status');
  status.textContent = message ?? '';
  status.hidden = !message;
  el.appendChild(status);
  if (card.refused) {
    const choice = (label: string, hint: string, act: () => void) => {
      const b = document.createElement('button');
      b.className = 'rewind-choice';
      const strong = document.createElement('span');
      strong.className = 'rewind-choice-title';
      strong.textContent = label;
      const small = document.createElement('span');
      small.className = 'rewind-choice-hint';
      small.textContent = hint;
      b.append(strong, small);
      b.addEventListener('click', act);
      el.appendChild(b);
    };
    choice(card.conversationOnly, card.conversationOnlyHint, () => {
      disableAll();
      post({ type: 'rewind', ordinal, question: questionOf(row), conversationOnly: true, expectedRuns: card.runIds });
    });
    if (card.runIds.length > 0) {
      choice(card.reviewRuns, card.reviewRunsHint, () => {
        for (const runId of card.runIds) {
          reviewAnchors.set(runId, el);
          post({ type: 'runReview', runId });
        }
      });
    }
  }
  const actions = document.createElement('div');
  actions.className = 'approval-actions rewind-actions';
  const cancel = document.createElement('button');
  cancel.textContent = card.cancel;
  cancel.addEventListener('click', () => el.remove());
  actions.appendChild(cancel);
  if (!card.refused) {
    const go = document.createElement('button');
    go.className = 'primary';
    go.textContent = card.go;
    go.addEventListener('click', () => {
      disableAll();
      post({ type: 'rewind', ordinal, question: questionOf(row), conversationOnly: false, expectedRuns: card.runIds });
    });
    actions.appendChild(go);
  }
  el.appendChild(actions);
  row.after(el);
}

/** An answer, a notice or an error, in the current turn. */
function addAnswer(role: string, item: WvTranscriptItem): HTMLElement {
  const target = ensureTurn();
  const el = document.createElement('div');
  el.className = 'bubble ' + role + (item.notice ? ' notice' : '');
  const body = document.createElement('div');
  body.className = 'bubble-body';
  if (item.importCard) {
    body.appendChild(importCardElement(item.importCard));
  } else {
    renderMarkdownInto(body, item.text);
  }
  el.appendChild(body);
  // "Ask it to continue" as a gesture: on the last turn's end notice, gone once another turn starts (setBusy). Drawn from
  // the item, so a redraw of the view keeps it. Kept on click: a send the extension refuses — the backend down —
  // leaves it there.
  if (item.continues) {
    const go = document.createElement('button');
    go.className = 'bubble-action continue-turn';
    // ⚠ The end notice is drawn before the turn is marked over: hidden until then, shown by setBusy(false) — drawn only
    // when not busy, the live notice never had its button.
    go.hidden = busy;
    setIcon(go, 'play', t('continueTurn'), 12);
    go.addEventListener('click', () => post({ type: 'continue' }));
    el.appendChild(go);
  }
  // Copy what the answer shows: it keeps the model's inline reasoning in its text. A question is copied whole.
  el.appendChild(metaRow(item, role === 'assistant' ? stripThinkTags(item.text) : undefined));
  target.body.appendChild(el);
  scrollToBottom();
  return el;
}

/**
 * An `/import` preview: a box per item, the reasons for the rest folded under them, and Apply — which runs the command
 * with the boxes left ticked. Used once: the card stays, answered, its boxes as they were sent.
 */
function importCardElement(card: WvImportCard): HTMLElement {
  const el = document.createElement('div');
  el.className = 'import-card' + (card.applied ? ' answered' : '');
  const head = document.createElement('div');
  head.className = 'import-head';
  const title = document.createElement('div');
  title.className = 'import-title';
  title.textContent = card.title;
  const summary = document.createElement('div');
  summary.className = 'import-summary';
  summary.textContent = card.summary;
  head.append(title, summary);
  el.appendChild(head);

  const boxes: Array<{ number: number; input: HTMLInputElement }> = [];
  if (card.items.length > 0) {
    const list = document.createElement('ul');
    list.className = 'import-items';
    for (const item of card.items) {
      const row = document.createElement('li');
      row.className = 'import-item';
      // The box's name is the whole line: a screen reader says what ticking it writes.
      const label = document.createElement('label');
      const input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = item.checked;
      input.disabled = card.applied === true;
      const target = document.createElement('span');
      target.className = 'import-target';
      target.textContent = item.target;
      const text = document.createElement('span');
      text.className = 'import-label';
      text.textContent = item.label;
      label.append(input, target, text);
      row.appendChild(label);
      for (const note of item.notes) {
        const line = document.createElement('div');
        line.className = 'import-note';
        line.textContent = note;
        row.appendChild(line);
      }
      list.appendChild(row);
      boxes.push({ number: item.number, input });
    }
    el.appendChild(list);
  }

  if (card.skipped.length > 0) {
    const details = document.createElement('details');
    details.className = 'import-skipped';
    const fold = document.createElement('summary');
    fold.textContent = card.skippedTitle;
    const reasons = document.createElement('ul');
    for (const group of card.skipped) {
      const line = document.createElement('li');
      line.append(group.reason + ' — ');
      group.paths.forEach((path, i) => {
        if (i > 0) {
          line.append(', ');
        }
        const code = document.createElement('code');
        code.textContent = path;
        line.appendChild(code);
      });
      reasons.appendChild(line);
    }
    details.append(fold, reasons);
    el.appendChild(details);
  }

  if (card.items.length > 0) {
    const actions = document.createElement('div');
    actions.className = 'approval-actions';
    const apply = document.createElement('button');
    apply.className = 'primary';
    apply.textContent = card.applyLabel;
    apply.disabled = card.applied === true;
    apply.addEventListener('click', () => {
      apply.disabled = true;
      for (const box of boxes) {
        box.input.disabled = true;
      }
      el.classList.add('answered');
      post({ type: 'importApply', id: card.id, numbers: boxes.filter((b) => b.input.checked).map((b) => b.number) });
    });
    actions.appendChild(apply);
    el.appendChild(actions);
  }
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

/** The note lines of the thread, by id: a note's state changes once the model reads it. */
const noteLines = new Map<string, HTMLElement>();

function noteState(state: 'waiting' | 'read', afterCalls?: number): string {
  if (state === 'waiting') {
    return t('noteWaiting');
  }
  return !afterCalls ? t('noteReadFirst') : afterCalls === 1 ? t('noteReadOne') : t('noteReadMany', afterCalls);
}

/**
 * A note typed during the run: a line of the run where it was typed — never a question bubble, which would close the
 * turn being written — saying whether the model has read it yet.
 */
function addNoteLine(id: string | undefined, text: string, state: 'waiting' | 'read', afterCalls?: number): void {
  const list = ensureRun(ensureTurn());
  const line = document.createElement('div');
  line.className = 'step note' + (state === 'waiting' ? ' waiting' : '');
  const head = document.createElement('div');
  head.className = 'note-state';
  head.append(icon('edit', 13), document.createTextNode(noteState(state, afterCalls)));
  const body = document.createElement('div');
  body.className = 'note-text';
  body.textContent = text;
  line.append(head, body);
  list.appendChild(line);
  if (id) {
    noteLines.set(id, line);
  }
  scrollToBottom();
}

function showNote(id: string, text: string, state: 'waiting' | 'read', afterCalls?: number): void {
  const line = noteLines.get(id);
  if (!line) {
    addNoteLine(id, text, state, afterCalls);
    return;
  }
  line.classList.toggle('waiting', state === 'waiting');
  const head = line.querySelector('.note-state');
  if (head) {
    head.textContent = '';
    head.append(icon('edit', 13), document.createTextNode(noteState(state, afterCalls)));
  }
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
  // A change too large to show comes as one line that says so: no count, no button that would open only that line.
  const changed = item.toolDiff !== undefined && item.toolDiff.split('\n').some((l) => diffLineKind(l) !== 'other');
  if (changed) {
    line.appendChild(diffStat(item.toolDiff!));
  }
  step.appendChild(line);

  if (item.hasErrors) {
    // "Fix with AI": pre-fills the prompt with the failing output (VS parity).
    const fix = document.createElement('button');
    fix.className = 'bubble-action';
    setIcon(fix, 'tool', t('fixWithAi'), 13);
    fix.addEventListener('click', () => {
      setPrompt(t('fixPrompt') + '\n\n```\n' + (item.toolOutput ?? '') + '\n```');
      promptEl.focus();
    });
    step.appendChild(fix);
  }

  const body = document.createElement('div');
  body.className = 'tool-body';
  if (item.toolDiff) {
    // What the call wrote, in the lines the approval card showed: the change says more than the call's arguments.
    body.appendChild(renderToolDiff(item.toolDiff));
    if (changed) {
      const openDiff = document.createElement('button');
      openDiff.className = 'linkbtn';
      openDiff.textContent = t('approvalOpenDiff');
      openDiff.addEventListener('click', () => post({ type: 'openApprovalDiff', text: item.toolDiff ?? '' }));
      body.appendChild(openDiff);
    }
  } else if (item.toolInput) {
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
  // Errors always start expanded — the red output is the point of the step. A change does not: its +/− count is on the
  // line, and the setting that keeps steps folded is kept, as in Visual Studio.
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

/** A diff line's kind, by its prefix (`DiffComputer`): every line carries one character before its text. */
function diffLineKind(line: string): 'add' | 'del' | 'other' {
  return line.startsWith('+') ? 'add' : line.startsWith('-') ? 'del' : 'other';
}

/** The `+3 −2` of a step's change. */
function diffStat(diff: string): HTMLElement {
  const lines = diff.split('\n');
  const stat = document.createElement('span');
  stat.className = 'step-diffstat';
  const added = document.createElement('span');
  added.className = 'diff-add';
  added.textContent = '+' + lines.filter((l) => diffLineKind(l) === 'add').length;
  const removed = document.createElement('span');
  removed.className = 'diff-del';
  removed.textContent = '\u2212' + lines.filter((l) => diffLineKind(l) === 'del').length;
  stat.append(added, removed);
  return stat;
}

/** A step's change, coloured line by line — added green, removed red, the collapsed context muted. */
function renderToolDiff(diff: string): HTMLElement {
  const pre = document.createElement('pre');
  pre.className = 'tool-diff';
  for (const line of diff.split('\n')) {
    const span = document.createElement('span');
    span.textContent = line + '\n';
    const kind = diffLineKind(line);
    span.className = kind === 'add' ? 'diff-add' : kind === 'del' ? 'diff-del' : line.startsWith('\u2026') ? 'diff-skip' : '';
    pre.appendChild(span);
  }
  return pre;
}

/**
 * The banner of a build task that failed: how many errors the Problems panel holds, the first, the task — Fix them sends
 * the panel's errors to the model as the welcome banner does, Show the errors opens the panel. Closed, it stays closed
 * until the next build fails.
 */
function renderBuildBanner(banner: WvBuildBanner | null): void {
  if (banner && banner.seq === buildBannerClosed) {
    return;
  }
  // Redrawn at each change of the Problems panel: unchanged, it is left as it is — the focus on its buttons too.
  const shown = JSON.stringify(banner);
  if (shown === buildBannerShown) {
    return;
  }
  buildBannerShown = shown;
  buildBannerEl.textContent = '';
  const was = buildBannerEl.hidden;
  buildBannerEl.hidden = banner === null;
  if (!banner) {
    if (!was && !welcomeEl.hidden) {
      buildWelcome();
    }
    return;
  }
  const head = document.createElement('div');
  head.className = 'build-banner-head';
  const title = document.createElement('strong');
  title.textContent = banner.errors > 0 ? t('buildFailed', banner.errors) : t('buildFailedUncounted');
  const close = iconButton('close', t('close'), 14);
  const dismiss = (): void => {
    buildBannerClosed = banner.seq;
    renderBuildBanner(null);
    post({ type: 'dismissBuildBanner' });
  };
  close.addEventListener('click', dismiss);
  head.append(icon('failed', 14), title, close);
  buildBannerEl.appendChild(head);
  if (banner.firstError) {
    const first = document.createElement('code');
    first.className = 'build-banner-error';
    first.textContent = banner.firstError;
    first.title = banner.firstError;
    buildBannerEl.appendChild(first);
  }
  const from = document.createElement('div');
  from.className = 'build-banner-from';
  // No error in the panel (a task without a problem matcher, or errors fixed since): the task's output says why.
  from.textContent = t(banner.errors > 0 ? 'buildFailedFrom' : 'buildFailedFromNoErrors', banner.task);
  buildBannerEl.appendChild(from);
  const actions = document.createElement('div');
  actions.className = 'build-banner-actions';
  // Offered only with errors to send: with none in the panel, the click would answer "no problems" and send nothing.
  // The extension takes the banner down once they are sent — refused (a turn running), it stays.
  if (banner.errors > 0) {
    const fix = document.createElement('button');
    fix.textContent = t('welcomeFixThem');
    fix.addEventListener('click', () => post({ type: 'fixBuild', text: t('welcomeFixPrompt') }));
    actions.appendChild(fix);
  }
  if (banner.errors > 0) {
    const show = document.createElement('button');
    show.className = 'secondary';
    show.textContent = t('buildShowErrors');
    show.addEventListener('click', () => post({ type: 'showProblems' }));
    actions.appendChild(show);
    buildBannerEl.appendChild(actions);
  }
  // The welcome screen's own Problems banner gives way to it: one Fix them, one count.
  if (!welcomeEl.hidden) {
    buildWelcome();
  }
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
    const runId = run.runId;
    bar.dataset.runId = runId;
    // Any run's files can be reviewed: a file changed since by a later run is refused its undo, and says so.
    const review = document.createElement('button');
    review.className = 'review';
    setIcon(review, 'list', t('runReview'), 13);
    review.addEventListener('click', () => post({ type: 'runReview', runId }));
    bar.appendChild(review);
    // Undo reverts the most recent run that changed files (/undo-run): only that run's bar offers it.
    for (const old of messagesEl.querySelectorAll('.result .undo')) {
      old.remove();
    }
    const undo = document.createElement('button');
    undo.className = 'undo';
    setIcon(undo, 'regenerate', t('runUndo'), 13);
    // Its own run, named: the most recent run that changed files may be another one by now (a /tdd, a /task apply).
    undo.addEventListener('click', () => post({ type: 'send', text: `/undo-run #${runId}` }));
    bar.appendChild(undo);
  }
  target.el.appendChild(bar);
}

/**
 * A run's review card, under its result bar: each file against the state before the run, its diff folded, and Keep,
 * Undo (Delete for a file the run created) or Redo. Drawn again from each answer, the files left open stay open.
 */
function renderRunReview(runId: string, review: RunReview | null | undefined, message?: string): void {
  const bar = [...messagesEl.querySelectorAll<HTMLElement>('.result')].find((b) => b.dataset.runId === runId);
  const old = [...messagesEl.querySelectorAll<HTMLElement>('.run-review')].find((c) => c.dataset.runId === runId) ?? null;
  // A run whose bar is not on screen (a reloaded conversation): its card goes under the refused "Go back here" that
  // asked for it.
  const asked = reviewAnchors.get(runId);
  const anchor = bar ?? (asked?.isConnected ? asked : null);
  if (!anchor) {
    return;
  }
  if (review === undefined) {
    // Nothing to draw it from: the card shown takes its actions back, and says why.
    old?.querySelectorAll<HTMLButtonElement>('button.run-review-action').forEach((b) => (b.disabled = false));
    const said = old?.querySelector<HTMLElement>('.run-review-status');
    if (said && message) {
      said.textContent = message;
      said.hidden = false;
    }
    return;
  }
  const open = new Set([...(old?.querySelectorAll<HTMLElement>('.run-review-file.open') ?? [])].map((f) => f.dataset.path));
  const card = document.createElement('div');
  card.className = 'approval run-review';
  card.dataset.runId = runId;
  const head = document.createElement('div');
  head.className = 'approval-head';
  const title = document.createElement('span');
  title.className = 'approval-title';
  const subject = document.createElement('span');
  subject.className = 'approval-subject';
  const close = iconButton('close', t('close'));
  close.addEventListener('click', () => card.remove());
  head.append(title, subject, close);
  card.appendChild(head);
  const status = document.createElement('div');
  status.className = 'run-review-status';
  status.setAttribute('role', 'status');
  status.textContent = message ?? '';
  status.hidden = !message;
  if (!review) {
    // The run is no longer kept (its copies were pruned): only the sentence remains.
    card.append(status);
    (old ?? anchor).after(card);
    old?.remove();
    return;
  }
  title.textContent = review.title;
  subject.textContent = review.subtitle;
  const disableAll = () => card.querySelectorAll('button.run-review-action').forEach((b) => ((b as HTMLButtonElement).disabled = true));
  for (const f of review.files) {
    const row = document.createElement('div');
    row.className = 'run-review-file ' + f.state;
    row.dataset.path = f.path;
    const line = document.createElement('div');
    line.className = 'run-review-line';
    const toggle = document.createElement('button');
    toggle.className = 'run-review-toggle';
    toggle.title = f.path;
    const chevron = document.createElement('span');
    chevron.className = 'run-review-chevron';
    const name = document.createElement('span');
    name.className = 'run-review-name';
    name.textContent = f.name;
    toggle.append(chevron, name);
    const plus = document.createElement('span');
    plus.className = 'plus';
    plus.textContent = `+${f.added}`;
    const minus = document.createElement('span');
    minus.className = 'minus';
    minus.textContent = `−${f.removed}`;
    line.append(toggle, plus, minus);
    if (f.created) {
      const tag = document.createElement('span');
      tag.className = 'run-review-tag';
      tag.textContent = review.new;
      line.appendChild(tag);
    }
    if (f.stateLabel) {
      const state = document.createElement('span');
      state.className = 'run-review-state';
      state.textContent = f.stateLabel;
      line.appendChild(state);
    }
    const actions = document.createElement('span');
    actions.className = 'run-review-actions';
    // A file's actions, or one change's (`piece`, 1-based) in its own row.
    const action = (into: HTMLElement, label: string, what: 'undo' | 'redo' | 'keep', primary = false, piece?: number) => {
      const b = document.createElement('button');
      b.className = 'run-review-action' + (primary ? ' primary' : '');
      b.textContent = label;
      b.addEventListener('click', () => {
        disableAll();
        post({ type: 'runFile', runId, path: f.path, action: what, piece });
      });
      into.appendChild(b);
    };
    if (f.state === 'pending') {
      action(actions, review.keep, 'keep');
    }
    if (f.state === 'pending' || f.state === 'kept') {
      action(actions, f.created ? review.delete : review.undo, 'undo', true);
    }
    // Undone whole, or changes undone one by one: given the run's version again together.
    if (f.canRedo) {
      action(actions, review.redo, 'redo');
    }
    line.appendChild(actions);
    row.appendChild(line);
    if (f.state === 'changedSince') {
      const why = document.createElement('div');
      why.className = 'run-review-why';
      why.textContent = review.changedWhy;
      row.appendChild(why);
    }
    if (f.wholeOnly) {
      const whole = document.createElement('div');
      whole.className = 'run-review-why';
      whole.textContent = f.wholeOnly;
      row.appendChild(whole);
    }
    const diffOf = (lines: RunReviewLine[]) => {
      const pre = document.createElement('pre');
      pre.className = 'approval-preview';
      for (const l of lines) {
        const span = document.createElement('span');
        span.className = 'pl' + (l.prefix === '+' ? ' add' : l.prefix === '-' ? ' del' : l.prefix === '…' ? ' gap' : '');
        span.textContent = `${l.prefix} ${l.text}`;
        pre.appendChild(span);
      }
      return pre;
    };
    // A file of several changes shows each with its own actions; otherwise its diff.
    let diff: HTMLElement;
    if (f.pieces.length > 0) {
      diff = document.createElement('div');
      diff.className = 'run-review-pieces';
      const summary = document.createElement('div');
      summary.className = 'run-review-why';
      summary.textContent = f.piecesSummary;
      diff.appendChild(summary);
      for (const p of f.pieces) {
        const piece = document.createElement('div');
        piece.className = 'run-review-piece ' + p.state;
        piece.setAttribute('role', 'group');
        piece.setAttribute('aria-label', [p.title, p.line, p.stateLabel].filter((s) => s).join(', '));
        const pieceHead = document.createElement('div');
        pieceHead.className = 'run-review-piece-head';
        const pieceTitle = document.createElement('span');
        pieceTitle.textContent = p.title;
        const pieceLine = document.createElement('span');
        pieceLine.className = 'run-review-state';
        pieceLine.textContent = p.line;
        pieceHead.append(pieceTitle, pieceLine);
        if (p.stateLabel) {
          const pieceState = document.createElement('span');
          pieceState.className = 'run-review-state run-review-piece-state';
          pieceState.textContent = p.stateLabel;
          pieceHead.appendChild(pieceState);
        }
        const pieceActions = document.createElement('span');
        pieceActions.className = 'run-review-actions';
        if (p.state === 'pending') {
          action(pieceActions, review.keep, 'keep', false, p.number);
        }
        if (p.state === 'pending' || p.state === 'kept') {
          action(pieceActions, review.undo, 'undo', true, p.number);
        }
        if (p.state === 'undone') {
          action(pieceActions, review.redo, 'redo', false, p.number);
        }
        pieceHead.appendChild(pieceActions);
        piece.append(pieceHead, diffOf(p.diff));
        diff.appendChild(piece);
      }
    } else {
      diff = diffOf(f.diff);
    }
    const show = (shown: boolean) => {
      diff.hidden = !shown;
      row.classList.toggle('open', shown);
      setIcon(chevron, shown ? 'chevronDown' : 'chevronRight', undefined, 13);
      toggle.setAttribute('aria-expanded', String(shown));
    };
    show(open.has(f.path));
    toggle.addEventListener('click', () => show(diff.hidden));
    row.appendChild(diff);
    card.appendChild(row);
  }
  const note = document.createElement('div');
  note.className = 'run-review-note';
  note.textContent = review.note;
  card.append(status, note);
  (old ?? anchor).after(card);
  old?.remove();
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
  if (!last || busy || !canRegenerate) {
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
  renderPlaceholder();

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
    renderPlaceholder();
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
  actions.append(allow, always);
  // The narrow rule, offered only when one describes the call: written to the machine's rules, where "Always" lasts
  // the session. Its label comes from the host, in the user's language.
  if (card?.ruleLabel) {
    const rule = document.createElement('button');
    rule.className = 'rule';
    rule.textContent = card.ruleLabel;
    rule.title = card.ruleTooltip ?? '';
    rule.addEventListener('click', () => answer(3, rule));
    actions.append(rule);
  }
  actions.append(deny);
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

/**
 * Answers the card waiting (1 allow once, 0 deny) through its own button; false when none waits — or more than one:
 * a key then answered the oldest, often scrolled out of view, while the user read another.
 */
function answerWaitingCard(value: 0 | 1): boolean {
  if (approvalCards.size !== 1) {
    return false;
  }
  const waiting = approvalCards.values().next().value as HTMLElement | undefined;
  const button = waiting?.querySelector<HTMLButtonElement>(
    value === 1 ? '.approval-actions button.primary' : '.approval-actions button.deny');
  if (!button || button.disabled) {
    return false;
  }
  button.click();
  return true;
}

// The run this card belonged to was cancelled host-side: freeze it (same inert look as
// an answered card) so its buttons cannot answer into a run that no longer exists.
function dismissApprovalCard(id: number): void {
  const card = approvalCards.get(id);
  approvalCards.delete(id);
  renderPlaceholder();
  if (!card) {
    return; // already answered
  }
  card.classList.add('answered');
  for (const b of card.querySelectorAll('.approval-actions button')) {
    (b as HTMLButtonElement).disabled = true;
  }
}

// ── Input card: an MCP server's form, filled by the user — Send, Decline, Cancel ──
// Every word comes from the host's card (InputCard in the Core), already in the user's language; the host checks the
// values and sends the same card back with what to fix, which replaces the one drawn.
const inputCards = new Map<string, HTMLElement>();
let inputSeq = 0;

function addInputCard(card: InputCard): void {
  // ⚠ A card drawn before the view was rebuilt is no longer in it: replaced there, the new one would never show.
  const existing = inputCards.get(card.id)?.isConnected ? inputCards.get(card.id) : undefined;
  const hadFocus = !!existing && existing.contains(document.activeElement);
  const el = inputCardElement(card);
  if (existing) {
    existing.replaceWith(el);
  } else {
    if (streamEl) {
      actNarration.push(streamEl);
    }
    finishStream();
    const target = ensureTurn();
    target.when.textContent = t('turnWaiting');
    target.body.appendChild(el);
  }
  inputCards.set(card.id, el);
  // ⚠ Never takes the keyboard on its own: a key typed for the composer or the code must not land in a form. Only
  // when the user just sent it from inside, the first field to fix gets the focus.
  if (hadFocus) {
    el.querySelector<HTMLElement>('[aria-invalid="true"], .input-choice-row button[aria-invalid="true"]')?.focus();
  }
  scrollToBottom();
}

function inputCardElement(card: InputCard): HTMLFormElement {
  const form = document.createElement('form');
  form.className = 'input-card';
  form.noValidate = true; // the host checks the values, in the user's language
  form.setAttribute('aria-label', card.title);

  const head = document.createElement('div');
  head.className = 'input-head';
  const titles = document.createElement('div');
  titles.className = 'input-titles';
  const title = document.createElement('span');
  title.className = 'input-title';
  title.textContent = card.title;
  const sub = document.createElement('span');
  sub.className = 'input-sub';
  sub.textContent = [card.subtitle, card.requiredNote].filter((p) => p).join(' · ');
  titles.append(title, sub);
  head.append(icon('plug', 16), titles);

  const body = document.createElement('div');
  body.className = 'input-body';
  // The server's own words, as plain text: never Markdown, never a link.
  const message = document.createElement('p');
  message.className = 'input-message';
  message.textContent = '“' + card.message + '”';
  body.appendChild(message);
  if (card.errorSummary) {
    const alert = document.createElement('div');
    alert.className = 'input-alert';
    alert.setAttribute('role', 'alert');
    alert.append(icon('warning', 14), document.createTextNode(card.errorSummary));
    body.appendChild(alert);
  }
  const readers: Array<() => [string, string | boolean | string[]]> = [];
  for (const field of card.fields) {
    const [row, read] = inputFieldElement(field);
    body.appendChild(row);
    readers.push(read);
  }
  const reminder = document.createElement('div');
  reminder.className = 'input-reminder';
  reminder.append(icon('info', 13), document.createTextNode(card.reminder));
  body.appendChild(reminder);

  const actions = document.createElement('div');
  actions.className = 'approval-actions input-actions';
  const answer = (action: InputFormAnswer['action']): void => {
    if (inputCards.get(card.id) !== form || form.classList.contains('answered')) {
      return;
    }
    const values: InputFormAnswer['values'] = {};
    if (action === 'accept') {
      for (const read of readers) {
        const [key, value] = read();
        values[key] = value;
      }
    }
    form.classList.add('answered');
    for (const b of actions.querySelectorAll('button')) {
      (b as HTMLButtonElement).disabled = true;
    }
    post({ type: 'inputAnswer', id: card.id, action, values });
  };
  const send = document.createElement('button');
  send.type = 'submit';
  send.className = 'primary';
  send.append(document.createTextNode(card.sendLabel), kbd('Enter'));
  const decline = document.createElement('button');
  decline.type = 'button';
  decline.textContent = card.declineLabel;
  decline.title = card.declineTooltip;
  decline.addEventListener('click', () => answer('decline'));
  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = 'deny ghost';
  cancel.title = card.cancelTooltip;
  cancel.append(document.createTextNode(card.cancelLabel), kbd('Esc'));
  cancel.addEventListener('click', () => answer('cancel'));
  actions.append(send, decline, cancel);
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    answer('accept');
  });
  form.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      answer('cancel');
    }
  });
  form.append(head, body, actions);
  return form;
}

/** One field, and how to read its value when the form is sent. */
function inputFieldElement(field: InputCardField): [HTMLElement, () => [string, string | boolean | string[]]] {
  const id = `input-${++inputSeq}`;
  const row = document.createElement('div');
  row.className = 'input-field' + (field.error ? ' invalid' : '');
  const described: string[] = [];
  const note = (text: string, cls: string): void => {
    if (!text) {
      return;
    }
    const line = document.createElement('span');
    line.id = `${id}-${cls}`;
    line.className = cls;
    if (cls === 'input-error') {
      line.append(icon('failed', 12));
    }
    line.append(document.createTextNode(text));
    row.appendChild(line);
    described.push(line.id);
  };
  const labelText = (into: HTMLElement): void => {
    into.append(document.createTextNode(field.label));
    if (field.required) {
      const star = document.createElement('span');
      star.className = 'input-star';
      star.setAttribute('aria-hidden', 'true');
      star.textContent = ' *';
      into.appendChild(star);
    }
  };

  if (field.kind === 'boolean') {
    const label = document.createElement('label');
    label.className = 'input-check';
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.id = id;
    box.checked = field.flag;
    label.append(box);
    labelText(label);
    row.appendChild(label);
    note(field.hint, 'input-hint');
    note(field.error, 'input-error');
    box.setAttribute('aria-describedby', described.join(' '));
    return [row, () => [field.key, box.checked]];
  }

  if (field.kind === 'choices') {
    const set = document.createElement('fieldset');
    set.className = 'input-set';
    const legend = document.createElement('legend');
    labelText(legend);
    set.appendChild(legend);
    const boxes: Array<[HTMLInputElement, string]> = [];
    for (const option of field.options) {
      const label = document.createElement('label');
      label.className = 'input-check';
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = option.selected;
      label.append(box, document.createTextNode(option.label));
      set.appendChild(label);
      boxes.push([box, option.value]);
    }
    row.appendChild(set);
    note(field.hint, 'input-hint');
    note(field.error, 'input-error');
    set.setAttribute('aria-describedby', described.join(' '));
    if (field.error) {
      set.setAttribute('aria-invalid', 'true');
    }
    return [row, () => [field.key, boxes.filter(([b]) => b.checked).map(([, v]) => v)]];
  }

  const label = document.createElement('label');
  label.className = 'input-label';
  label.htmlFor = id;
  labelText(label);
  row.appendChild(label);

  if (field.kind === 'choice' && field.buttons) {
    // Few short choices: buttons side by side, one pressed.
    let picked = field.options.find((o) => o.selected)?.value ?? '';
    const group = document.createElement('div');
    group.className = 'input-choice-row';
    group.setAttribute('role', 'radiogroup');
    group.setAttribute('aria-label', field.label);
    group.id = id;
    const buttons: HTMLButtonElement[] = [];
    for (const option of field.options) {
      const b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'radio');
      b.setAttribute('aria-checked', String(option.value === picked));
      b.textContent = option.label;
      if (field.error) {
        b.setAttribute('aria-invalid', 'true');
      }
      b.addEventListener('click', () => {
        picked = option.value;
        for (const other of buttons) {
          other.setAttribute('aria-checked', String(other === b));
        }
      });
      buttons.push(b);
      group.appendChild(b);
    }
    row.appendChild(group);
    note(field.hint, 'input-hint');
    note(field.error, 'input-error');
    group.setAttribute('aria-describedby', described.join(' '));
    return [row, () => [field.key, picked]];
  }

  if (field.kind === 'choice') {
    const select = document.createElement('select');
    select.id = id;
    // No choice made yet: an empty first entry, never a choice the user did not make.
    if (!field.options.some((o) => o.selected)) {
      select.appendChild(new Option('', '', true, true));
    }
    for (const option of field.options) {
      select.appendChild(new Option(option.label, option.value, option.selected, option.selected));
    }
    row.appendChild(select);
    note(field.hint, 'input-hint');
    note(field.error, 'input-error');
    select.setAttribute('aria-describedby', described.join(' '));
    if (field.error) {
      select.setAttribute('aria-invalid', 'true');
    }
    return [row, () => [field.key, select.value]];
  }

  const input = document.createElement('input');
  input.id = id;
  input.value = field.text;
  // Typed as text and checked by the host, as in Visual Studio: the browser's own boxes would refuse a comma, drop a
  // time zone the server asks for, or show a date in the machine's order under a hint that says YYYY-MM-DD.
  input.type = field.inputType === 'email' || field.inputType === 'url' ? field.inputType : 'text';
  if (field.kind === 'number') {
    input.inputMode = 'decimal';
  }
  input.autocomplete = 'off';
  input.spellcheck = false;
  row.appendChild(input);
  note(field.hint, 'input-hint');
  note(field.error, 'input-error');
  input.setAttribute('aria-describedby', described.join(' '));
  if (field.error) {
    input.setAttribute('aria-invalid', 'true');
  }
  return [row, () => [field.key, input.value]];
}

// The card's turn stopped, or a note set it aside: frozen until its line replaces it.
function dismissInputCard(id: string): void {
  const card = inputCards.get(id);
  if (!card) {
    return;
  }
  card.classList.add('answered');
  for (const control of card.querySelectorAll('button, input, select')) {
    (control as HTMLButtonElement).disabled = true;
  }
}

/** The line an answered card leaves: drawn where the card was, or at the end of the turn when it is not drawn. */
function showInputReceipt(id: string, saved: string): void {
  const el = inputReceiptElement(saved);
  const card = inputCards.get(id);
  inputCards.delete(id);
  if (card?.isConnected) {
    card.replaceWith(el);
  } else {
    ensureTurn().body.appendChild(el);
  }
  if (busy && turn) {
    turn.when.textContent = t('turnWorking', turn.steps + 1);
  }
  scrollToBottom();
}

/** A receipt as the conversation keeps it (`InputReceipt.Saved`): its title, its detail, then what was sent. */
function inputReceiptElement(saved: string): HTMLElement {
  const [title = '', detail = '', ...rest] = saved.replace(/\r\n/g, '\n').split('\n');
  const body = rest.join('\n');
  const el = document.createElement('div');
  el.className = 'input-receipt';
  const line = document.createElement('button');
  line.type = 'button';
  line.className = 'step-line';
  const name = document.createElement('span');
  name.className = 'input-receipt-title';
  name.textContent = title;
  const sub = document.createElement('span');
  sub.className = 'step-subject';
  sub.textContent = detail;
  line.append(icon(body ? 'check' : 'info', 13), name, sub);
  el.appendChild(line);
  if (body) {
    line.setAttribute('aria-expanded', 'false');
    line.append(icon('chevronDown', 12));
    const values = document.createElement('div');
    values.className = 'input-receipt-body';
    values.textContent = body;
    values.hidden = true;
    line.addEventListener('click', () => {
      values.hidden = !values.hidden;
      line.setAttribute('aria-expanded', String(!values.hidden));
    });
    el.appendChild(values);
  }
  return el;
}

// ── Welcome: what to work on — the open file's actions, the errors, the keys ──
const welcomeEl = document.createElement('div');
welcomeEl.id = 'welcome';
welcomeEl.hidden = true;
messagesEl.appendChild(welcomeEl);
const welcomeLine = document.createElement('p');
welcomeLine.className = 'welcome-line';

/** The model, its server, and where the code goes: "stays on this machine" only when the host says no role in use
 *  runs elsewhere. A status that does not say (no host yet, a host down, a check that failed) gets no line at all:
 *  neither the promise nor a name it cannot back. */
function renderWelcomeLine(): void {
  const hosts = connection?.offMachine;
  if (!hosts) {
    welcomeLine.textContent = '';
    return;
  }
  const elsewhere = hosts.join(', ');
  welcomeLine.textContent = currentModel && connection?.server
    ? (elsewhere ? t('welcomeLineRemote', currentModel, connection.server, elsewhere) : t('welcomeLine', currentModel, connection.server))
    : (elsewhere ? t('welcomeRemote', elsewhere) : t('welcomeLocal'));
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

  if (problems > 0 && buildBannerEl.hidden) {
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
  welcomeEl.hidden = !transcriptEmpty || setupView !== null;
  if (!welcomeEl.hidden) {
    buildWelcome();
  }
}

// ── First run: the card that finds a server and proposes models (the extension runs the steps, setupFlow.ts) ──
const setupEl = document.createElement('div');
setupEl.id = 'setup';
setupEl.hidden = true;
messagesEl.insertBefore(setupEl, welcomeEl);
let setupView: WvSetupView | null = null;

function setupButton(label: string, action: WvSetupAction, primary = false): HTMLButtonElement {
  const b = document.createElement('button');
  b.className = primary ? 'setup-button primary' : 'setup-button';
  b.textContent = label;
  b.disabled = setupView?.busy === true;
  b.addEventListener('click', () => post({ type: 'setupAction', ...action }));
  return b;
}

function setupText(tag: 'h1' | 'p' | 'div' | 'span', cls: string, text: string): HTMLElement {
  const el = document.createElement(tag);
  el.className = cls;
  el.textContent = text;
  return el;
}

/** One place and what it answered; a place that asks for a key gets a field for it. */
function setupPlaceRow(place: SetupPlace, s: Record<string, string>): HTMLElement {
  const li = document.createElement('li');
  li.className = 'setup-place ' + place.outcome;
  li.appendChild(icon(place.outcome === 'checking' ? 'running' : place.outcome === 'answers' ? 'check' : 'pending', 14));
  const name = document.createElement('span');
  name.className = 'setup-place-name';
  name.append(setupText('span', '', place.name), setupText('span', 'setup-url', place.url));
  li.append(name, setupText('span', 'setup-place-status', place.status));
  if (place.outcome === 'needsKey') {
    li.appendChild(setupKeyForm(place.url, s));
  }
  return li;
}

/** An address and a key, sent to that address only. */
function setupKeyForm(url: string, s: Record<string, string>): HTMLElement {
  const form = document.createElement('form');
  form.className = 'setup-key';
  const key = document.createElement('input');
  key.type = 'password';
  key.dataset.keep = 'key:' + url;
  key.placeholder = s.apiKey;
  key.setAttribute('aria-label', `${s.apiKey} — ${url}`);
  const go = document.createElement('button');
  go.type = 'submit';
  go.className = 'setup-button';
  go.textContent = s.enterKey;
  go.disabled = setupView?.busy === true;
  form.append(key, go);
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    if (key.value.trim()) post({ type: 'setupAction', action: 'connect', url, key: key.value.trim() });
  });
  return form;
}

/** The address of a server on another machine, under `title`. */
function setupAddressForm(s: Record<string, string>, title: string): HTMLElement {
  const section = document.createElement('section');
  section.className = 'setup-box';
  const form = document.createElement('form');
  form.className = 'setup-address';
  const label = setupText('span', 'setup-label', title);
  const row = document.createElement('div');
  row.className = 'setup-row';
  const input = document.createElement('input');
  input.dataset.keep = 'address';
  input.placeholder = 'http://192.168.1.20:11434';
  input.setAttribute('aria-label', s.otherMachine);
  const go = document.createElement('button');
  go.type = 'submit';
  go.className = 'setup-button primary';
  go.textContent = s.connect;
  go.disabled = setupView?.busy === true;
  row.append(input, go);
  form.append(label, row, setupText('span', 'setup-hint', s.serverKinds));
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    if (input.value.trim()) post({ type: 'setupAction', action: 'connect', url: input.value.trim() });
  });
  section.appendChild(form);
  return section;
}

/** The server's graphics memory to choose, when the host could not find it (or it was chosen here, to change it). */
function setupCardChooser(plan: SetupPlan): HTMLElement | null {
  if (plan.cardChoices.length === 0) return null;
  const row = document.createElement('div');
  row.className = 'setup-card';
  row.setAttribute('role', 'group');
  row.setAttribute('aria-label', plan.cardQuestion ?? '');
  row.appendChild(setupText('span', 'setup-hint', plan.cardQuestion ?? ''));
  const choices = document.createElement('div');
  choices.className = 'setup-row';
  for (const c of plan.cardChoices) {
    const b = setupButton(c.label, { action: 'card', gb: c.gb });
    const chosen = plan.budgetGb === c.gb;
    b.classList.toggle('chosen', chosen);
    b.setAttribute('aria-pressed', String(chosen));
    choices.appendChild(b);
  }
  row.appendChild(choices);
  return row;
}

function renderSetup(): void {
  // ⚠ The card is drawn anew at each step (the scan ending, each download progress): what is being typed in its fields,
  // and the focus, carry over — or an address half-typed during the search vanishes when the search ends.
  const typed = new Map<string, string>();
  for (const field of setupEl.querySelectorAll<HTMLInputElement>('input[data-keep]')) {
    typed.set(field.dataset.keep!, field.value);
  }
  const focused = (document.activeElement as HTMLElement | null)?.dataset?.keep;
  renderSetupView();
  for (const field of setupEl.querySelectorAll<HTMLInputElement>('input[data-keep]')) {
    const value = typed.get(field.dataset.keep!);
    if (value) field.value = value;
    if (field.dataset.keep === focused) field.focus();
  }
}

function renderSetupView(): void {
  const v = setupView;
  setupEl.textContent = '';
  setupEl.hidden = v === null;
  if (!v) {
    showWelcomeIfEmpty();
    return;
  }
  welcomeEl.hidden = true;
  const s = v.strings;
  const plan = v.plan;

  switch (v.phase) {
    case 'searching': {
      setupEl.append(setupText('h1', 'setup-title', s.lookingTitle), setupText('p', 'setup-lead', s.lookingLead));
      const list = document.createElement('ul');
      list.className = 'setup-places';
      for (const p of v.places) list.appendChild(setupPlaceRow(p, s));
      setupEl.appendChild(list);
      break;
    }
    case 'nothing': {
      setupEl.append(setupText('h1', 'setup-title', s.nothingTitle), setupText('p', 'setup-lead', s.nothingLead));
      const list = document.createElement('ul');
      list.className = 'setup-places';
      for (const p of v.places) list.appendChild(setupPlaceRow(p, s));
      setupEl.appendChild(list);
      // Connected to WSL, an SSH server or a container: "here" is that machine, and the places above were looked at there.
      if (s.remoteNote) setupEl.appendChild(setupText('p', 'setup-hint', s.remoteNote));
      setupEl.appendChild(setupAddressForm(s, s.otherMachine));
      const install = document.createElement('section');
      install.className = 'setup-box';
      // The servers the sentence names: two that install with a model library and need no command line.
      const links = document.createElement('div');
      links.className = 'setup-row';
      for (const [name, url] of [['Ollama', 'https://ollama.com'], ['LM Studio', 'https://lmstudio.ai']]) {
        const a = document.createElement('a');
        a.href = url;
        a.textContent = name;
        links.appendChild(a);
      }
      install.append(setupText('span', 'setup-label', s.noServerYet), setupText('p', 'setup-hint', s.installHint), links,
        setupButton(s.lookAgain, { action: 'lookAgain' }));
      setupEl.appendChild(install);
      break;
    }
    case 'found': {
      if (!plan) break;
      setupEl.append(setupText('h1', 'setup-title', s.foundTitle), setupText('p', 'setup-lead', plan.lead));
      const foundCard = setupCardChooser(plan);
      if (foundCard) setupEl.appendChild(foundCard);
      const box = document.createElement('section');
      box.className = 'setup-box';
      const roles = document.createElement('div');
      roles.className = 'setup-roles';
      const role = (label: string, value: string): void => {
        roles.append(setupText('span', 'setup-role', label), setupText('span', 'setup-model', value));
      };
      role(s.chat, plan.chatModel ?? '');
      role(s.completion, !plan.completionModel || plan.completionModel === plan.chatModel ? s.sameModel : plan.completionModel);
      role(s.search, plan.searchModel || s.noSearchModel);
      box.append(setupText('span', 'setup-label', plan.legend), roles);
      if (plan.searchNote) box.appendChild(setupText('p', 'setup-hint', plan.searchNote));
      if (plan.fitText) {
        const fit = document.createElement('div');
        fit.className = 'setup-fit' + (plan.fits ? '' : ' tight');
        fit.append(icon(plan.fits ? 'check' : 'warning', 14), document.createTextNode(plan.fitText));
        box.appendChild(fit);
      }
      if (plan.windowText) box.appendChild(setupText('p', 'setup-hint', plan.windowText));
      const actions = document.createElement('div');
      actions.className = 'setup-row';
      actions.append(setupButton(s.start, { action: 'start' }, true), setupButton(s.chooseOther, { action: 'chooseOther' }));
      box.appendChild(actions);
      setupEl.appendChild(box);
      for (const other of v.others) {
        const line = document.createElement('div');
        line.className = 'setup-other';
        line.append(icon('check', 14), setupText('span', '', other.alsoAnswering ?? other.name),
          setupButton(s.useInstead, { action: 'use', url: other.url, provider: other.provider ?? '' }));
        setupEl.appendChild(line);
      }
      break;
    }
    case 'noModel': {
      if (!plan) break;
      setupEl.append(setupText('h1', 'setup-title', s.noModelTitle), setupText('p', 'setup-lead', plan.lead));
      if (plan.itsModels) setupEl.appendChild(setupText('p', 'setup-hint', plan.itsModels));
      const noModelCard = v.pull ? null : setupCardChooser(plan);
      if (noModelCard) setupEl.appendChild(noModelCard);
      if (plan.downloads.length > 0 && !v.pull) {
        const box = document.createElement('section');
        box.className = 'setup-box';
        box.appendChild(setupText('span', 'setup-label', plan.legend));
        for (const d of plan.downloads) {
          const row = document.createElement('div');
          row.className = 'setup-download';
          const text = document.createElement('span');
          text.className = 'setup-place-name';
          text.append(setupText('span', 'setup-model', d.name), setupText('span', 'setup-hint', d.measured));
          row.append(text, setupText('span', 'setup-size', d.size), setupButton(d.button, { action: 'download', tag: d.tag }, true));
          box.appendChild(row);
        }
        setupEl.appendChild(box);
      } else if (plan.noDownload) {
        setupEl.appendChild(setupText('p', 'setup-hint', plan.noDownload));
      }
      if (v.pull) {
        const box = document.createElement('section');
        box.className = 'setup-box';
        box.setAttribute('aria-live', 'polite');
        const bar = document.createElement('div');
        bar.className = 'setup-bar';
        bar.setAttribute('role', 'progressbar');
        bar.setAttribute('aria-valuemin', '0');
        bar.setAttribute('aria-valuemax', '100');
        bar.setAttribute('aria-label', v.pull.title);
        const fill = document.createElement('div');
        if (typeof v.pull.percent === 'number') {
          bar.setAttribute('aria-valuenow', String(v.pull.percent));
          fill.style.width = `${v.pull.percent}%`;
        }
        bar.appendChild(fill);
        box.append(setupText('span', 'setup-label', v.pull.title), bar, setupText('span', 'setup-hint', v.pull.status),
          setupText('span', 'setup-hint', s.downloadGoesOn));
        setupEl.appendChild(box);
      }
      setupEl.appendChild(setupButton(s.lookAgain, { action: 'lookAgain' }));
      break;
    }
  }

  // While looking, an address can be typed already: the server may be on another machine.
  if (v.phase === 'searching') {
    setupEl.appendChild(setupAddressForm(s, s.typeAddress));
  }
  if (v.error) {
    const err = document.createElement('div');
    err.className = 'welcome-banner';
    err.setAttribute('role', 'alert');
    err.append(icon('warning', 14), setupText('span', '', v.error));
    setupEl.appendChild(err);
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

// An address or a revision holds no space and may hold an '@': its value ends at the first space, past which the line
// is the question (MentionController.MentionQueryRegex reads it the same way).
const COMMITTED_RE = /@(?:(file|code|folder) ([^\n@]*)|(url|commit) (\S*))$/i;
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
    const category = (committed[1] ?? committed[3]).toLowerCase();
    const query = committed[2] ?? committed[4];
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
    if (category === 'url') {
      // An address has no intermediate hits either: one action row fetching it.
      const url = query.trim();
      openMentionRows(url.length === 0 ? [] : [{
        build: (row) => { setIcon(row, 'attach', t('mentionAttachUrl', url), 13); },
        action: () => {
          stripMentionToken();
          post({ type: 'resolveMention', category: 'url', value: url });
        },
      }]);
      return;
    }
    if (category === 'commit') {
      // The typed line now, never after the search: Enter takes the first row, and an older one would attach the
      // revision typed before. The latest commits join it when the host answers.
      openMentionRows([commitTypedRow(query.trim())]);
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
    setPrompt(before + value.slice(caret));
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
  setPrompt(before + value.slice(caret));
  promptEl.setSelectionRange(before.length, before.length);
  promptEl.focus();
  closeMentions();
}

function insertMention(path: string): void {
  const caret = promptEl.selectionStart;
  const value = promptEl.value;
  // Quoted when the path holds a space: the extension reads a bare token up to the first space only.
  const written = mentionFor(path);
  setPrompt(value.slice(0, mentionStart) + written + ' ' + value.slice(caret));
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
/** @commit's first row: the typed revision — a branch, a tag, an older hash; the last commit before anything is typed. */
function commitTypedRow(revision: string): { build: (row: HTMLElement) => void; action: () => void } {
  return {
    build: (row) => { setIcon(row, 'history', t('mentionAttachCommit', revision || 'HEAD'), 13); },
    action: () => {
      stripMentionToken();
      post({ type: 'resolveMention', category: 'commit', value: revision });
    },
  };
}

function renderMentionResults(category: string, items: WvMentionItem[]): void {
  if (mentionStart < 0) {
    return;
  }
  const typed = category === 'commit' ? [commitTypedRow(latestMentionSearch.query.trim())] : [];
  openMentionRows([...typed, ...items.map((item) => ({
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
        setPrompt(before + value.slice(caret));
        promptEl.setSelectionRange(before.length, before.length);
        promptEl.focus();
        closeMentions();
      } else {
        stripMentionToken();
        post({ type: 'resolveMention', category, value: item.value });
      }
    },
  }))]);
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
    el.className = chip.auto ? 'chip auto' : 'chip';
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
  // Chips the search found while the person typed: what they are, and that they can go.
  if (chips.some((c) => c.auto)) {
    const note = document.createElement('div');
    note.className = 'chips-note';
    note.textContent = t('chipsFoundWhileTyping');
    chipsEl.appendChild(note);
  }
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
  setPrompt(command + ' ');
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
  setPrompt(historyEntries[historyEntries.length - 1 - historyIndex]);
}

function historyDown(): void {
  if (historyIndex < 0) {
    return;
  }
  historyIndex--;
  setPrompt(historyIndex >= 0 ? historyEntries[historyEntries.length - 1 - historyIndex] : historyDraft);
  if (historyIndex < 0) {
    historyDraft = '';
  }
}

// ── Composer events ──────────────────────────────────────────────────────────
/** @param later Ctrl+Enter: during a turn, the message is sent once the turn ends instead of being read by it. */
function send(later = false): void {
  const text = promptEl.value;
  // A step pause takes /resume, the remedy the step-mode notice names.
  if (busy && stepPausedNow && text.trim().toLowerCase() === '/resume') {
    promptEl.value = '';
    post({ type: 'resumeStep' });
    return;
  }
  if (!text.trim()) {
    return;
  }
  closeMentions();
  closeSlash();
  historyIndex = -1;
  historyDraft = '';
  promptEl.value = '';
  if (!busy) {
    post({ type: 'send', text, typed: true });
    return;
  }
  // ⚠ During a turn the text is never dropped (it was, in silence): Ctrl+Enter sends it after the turn, Enter makes it a
  // note the run reads at its next step — the extension decides, once the host took it, whether a card waiting is
  // refused by it (a text never approves) and a paused run resumes to read it.
  post(later ? { type: 'queue', text } : { type: 'steer', text });
}

renderPlaceholder();
/**
 * The composer's text set by the panel itself — a recalled question, a picked command or mention, a fix prompt: the
 * extension is told, as for a keystroke, or the chips found for the previous draft would go with the new one. Not as a
 * message is sent: its chips leave with it.
 */
function setPrompt(text: string): void {
  promptEl.value = text;
  post({ type: 'draft', text });
}

promptEl.addEventListener('input', () => {
  detectMention();
  detectSlash();
  // The extension looks for the code the draft is about once typing pauses (the chips found while typing).
  post({ type: 'draft', text: promptEl.value });
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
    // A card waiting and nothing typed: Enter is the "Allow once" the card names. With text, or with Ctrl (a message for
    // after the turn, as in Visual Studio), it never approves.
    if (!e.ctrlKey && !e.metaKey && promptEl.value.trim() === '' && answerWaitingCard(1)) {
      return;
    }
    send(e.ctrlKey || e.metaKey);
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
  messagesEl.append(setupEl, welcomeEl);
  streamEl = null;
  turn = null;
  transcriptEmpty = true;
  noteLines.clear();
  // The cards still waiting are posted again after a rebuild (hydrate), into the new view.
  inputCards.clear();
  for (const item of transcript) {
    if (item.role === 'tool') {
      addToolBubble(item, toolBubblesExpanded);
    } else if (item.role === 'input') {
      ensureTurn().body.appendChild(inputReceiptElement(item.text));
    } else if (item.role === 'user' && item.note) {
      addNoteLine(item.noteId, item.text, item.note, item.noteAfter);
    } else if (item.role === 'user' || item.role === 'assistant' || item.role === 'error') {
      const drawn = addBubble(item.role, item);
      if (item.role === 'error' && item.cut && drawn) {
        renderCut(drawn, item.cut);
      }
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
    case 'setup': {
      setupView = msg.view;
      renderSetup();
      break;
    }
    case 'models': {
      models = msg.models ?? models;
      if (msg.model !== undefined) {
        currentModel = msg.model;
        renderModelButton();
      }
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
      canRegenerate = msg.canRegenerate === true;
      planMode = msg.planMode === true;
      editorFile = msg.editorFile ?? null;
      problems = msg.problems ?? 0;
      attachedRecap = msg.attachedRecap ?? '';
      mentionCategories = msg.mentionCategories ?? [];
      renderChips(msg.chips ?? []);
      renderPins(msg.pins ?? []);
      renderQueue(msg.queue ?? []);
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
    case 'wait':
      renderWait(msg.wait);
      break;
    case 'assistant':
      // Out-of-turn assistant bubble (e.g. a background /task finishing) — persistent, unlike a
      // status line the next setBusy wipes. Its own section, never inside the turn being written.
      if (!busy) {
        turn = null;
      }
      // A notice: drawn as the thread draws it once reloaded (no model, never a reply to regenerate).
      addBubble('assistant', { role: 'assistant', text: msg.text, timestamp: msg.timestamp, notice: msg.notice });
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
          toolDiff: msg.diff,
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
    case 'inputForm':
      addInputCard(msg.card);
      break;
    case 'inputDismiss':
      dismissInputCard(msg.id);
      break;
    case 'inputReceipt':
      showInputReceipt(msg.id, msg.saved);
      break;
    case 'mentionSuggestions':
      if (msg.query === latestMentionQuery) {
        renderMentions(msg.items);
      }
      break;
    case 'xrayPanel':
      renderXray(msg.panel);
      break;
    case 'putBackQuestion':
      // Above a draft typed meanwhile, never over it.
      setPrompt(promptEl.value.trim() ? `${msg.text}\n\n${promptEl.value}` : msg.text);
      promptEl.focus();
      break;
    case 'rewindCard':
      renderRewindCard(msg.ordinal, msg.card, msg.message);
      break;
    case 'runReview':
      renderRunReview(msg.runId, msg.review, msg.message);
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
    case 'buildBanner':
      renderBuildBanner(msg.banner);
      break;
    case 'editorContext':
      editorFile = msg.file;
      problems = msg.problems;
      if (!welcomeEl.hidden) {
        buildWelcome();
      }
      break;
    case 'setPrompt':
      setPrompt(msg.text);
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
      stepPausedNow = true;
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
      stepPausedNow = false;
      for (const el of messagesEl.querySelectorAll('.step-pause')) {
        el.remove();
      }
      break;
    case 'note':
      showNote(msg.id, msg.text, msg.state, msg.afterCalls);
      break;
    case 'noteRemoved':
      noteLines.get(msg.id)?.remove();
      noteLines.delete(msg.id);
      break;
    case 'queue':
      renderQueue(msg.items);
      break;
    case 'restoreDraft':
      // In front of what is typed there: the draft is the user's too.
      setPrompt(promptEl.value.trim() ? `${msg.text}\n\n${promptEl.value}` : msg.text);
      break;
    case 'turnEnded': {
      stepPausedNow = false;
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
        addBubble('assistant', { role: 'assistant', text: msg.text, timestamp: msg.timestamp, notice: msg.notice === true });
      }
      // The line saying why the run stopped, AFTER the answer and without replacing it — a notice, as the
      // transcript keeps it: drawn as an answer, it changed look on reload.
      if (msg.endNotice) {
        addBubble('assistant', {
          role: 'assistant', text: msg.endNotice, timestamp: msg.timestamp, notice: true, continues: msg.offersContinue === true,
        });
      }
      if (msg.error) {
        const failed = addBubble('error', { role: 'error', text: msg.error, timestamp: msg.timestamp });
        if (failed && msg.cut) {
          renderCut(failed, msg.cut);
        }
      }
      if (msg.cancelled) {
        addBubble('error', { role: 'error', text: t('cancelled'), timestamp: msg.timestamp });
      }
      renderPlan(null);
      setBusy(false);
      // The answer's header: the model that answered and how long it took; under it, the run. A notice names none:
      // the turn opened under the current model's name before anyone knew no model would write it.
      if (turn) {
        const view: TurnView = turn;
        if (msg.notice) {
          view.who.textContent = '';
        } else if (msg.model) {
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
      canRegenerate = msg.canRegenerate === true;
      refreshRegenerate();
      scrollToBottom();
      break;
    }
  }
});

post({ type: 'ready' });
