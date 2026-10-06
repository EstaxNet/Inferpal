// Wire DTOs of the editor ⇄ Inferpal.Host JSON-RPC protocol.
// Mirror of Inferpal.Host/HostProtocol.cs — camelCase on the wire both ways.

export interface InitializeParams {
  rootDir: string;
  locale?: string;
  clientName?: string;
  /**
   * This adapter serves the reverse `debug/*` requests. Declared rather than assumed: without it
   * the host would register two debugger tools whose every call answers "method not found", and
   * the model would pay for them on every turn.
   */
  debug?: boolean;
  /**
   * The workspace's other folders (multi-root). The tools serve `rootDir` only: the folders it does not hold are
   * stated to the model, which otherwise looks for their code under the root and concludes it does not exist.
   */
  otherFolders?: string[];
}

export interface InitializeResult {
  hostVersion: string;
  provider: string;
  defaultModel: string;
  modelManagement: boolean;
  vramMonitoring: boolean;
  fim: boolean;
  keepAlive: boolean;
}

/** `fim/settings` — Inferpal's inline-completion switch and the debounce of its mode. */
export interface FimSettingsResult {
  enabled: boolean;
  debounceMs: number;
}

export interface ChatSendParams {
  prompt: string;
  model?: string;
  agentMode?: boolean;
  /** Workspace-relative paths of files inlined as attachments — the host's RAG auto-context
   * skips their chunks instead of injecting them a second time (parity with VS). */
  attachedPaths?: string[];
  /** A read-only code action (/explain, /review): the host answers with the code-actions model and no tools,
   * whatever the agent-mode switch says — as the Visual Studio window does. */
  codeAction?: boolean;
  /** The question as the user wrote it, before the attached files were appended: what the per-turn auto-context
   * searches for (Visual Studio searches the typed text). */
  query?: string;
}

export interface ChatSendResult {
  text: string;
  cancelled: boolean;
  tokensUsed: number;
  promptTokens: number;
  error?: string | null;
  /** Why the run stopped, when it is not because the model was done. The answer stays in `text`;
   *  this is added after it, never in its place. */
  endNotice?: string | null;
  /** The window this turn was measured against — the loaded one when the server reports a smaller one
   *  than configured. The context gauge shows this, not the setting. 0 = unknown. */
  contextWindow?: number;
  /** What the next question will send (conversation + tool definitions) — the gauge's fill, the X-Ray's
   *  figure. Not `promptTokens`, which measured this turn's last request, the run's own transcript included. */
  nextTurnTokens?: number;
  /** The turn's run — its steps, the files it changed, its last check; absent when no tool ran. */
  run?: RunSummary | null;
  /** How long the turn took ("21 s"), localized by the host. */
  duration?: string | null;
  /** The model that answered. */
  model?: string | null;
}

export interface ToolNotice {
  name: string;
  input: string;
  output: string;
  hasErrors: boolean;
}

export interface PlanNotice {
  goal: string;
  steps: string[];
}

export interface StepUpdateNotice {
  index: number;
  status: string;
}

export interface TextNote {
  text: string;
}

/** `chat/thinking` — the throttled reasoning tail, or no text at all when the host only means
 *  "the model is thinking" (plain chat). */
export interface ThinkingNote {
  text?: string | null;
}

export interface DocumentParams {
  path: string;
  text?: string;
  /** Whether the buffer has unsaved changes: only then does the host prefer it over the disk. */
  dirty?: boolean;
}

export interface ActiveDocumentDto {
  path: string | null;
  text: string | null;
}

export interface EditResultDto {
  path: string | null;
  replacedSelection: boolean;
}

export interface IndexStatusResult {
  isIndexing: boolean;
  chunkCount: number;
  rootDir: string;
}

/** `backend/status` answer — connection badge + compact VRAM line ("model · X.X GB"). */
export interface BackendStatusResult {
  connected: boolean;
  vramBadge: string;
  /** The line to put IN THE THREAD when this heartbeat just crossed an edge — went unreachable, or
   *  came back — and null the rest of the time. A dot that changes colour says "this is how it is
   *  now"; it does not say "it just dropped". The Core decides which edge, and whether the very
   *  first check is silent. */
  edgeNotice?: string | null;
  /** The configured backend, as the chat header names it ("LM Studio"). */
  server?: string | null;
  /** Not connected, but the server REFUSED the check (a wrong API key: 401): the badge text, already localized.
   *  A server that answers is running — "unreachable" sends the user to start it. */
  refused?: string | null;
}

/** `models/adoptDefault` — the model now used in place of a default nobody chose and the backend lacks, with the
 *  sentence that says so (already localized); both null when nothing changed. */
export interface ModelsAdoptResult {
  model: string | null;
  notice: string | null;
}

/** `connection/check` — what the Test button found AT THE URL IT WAS GIVEN.
 *  `provider` is the detected backend code (`ollama` | `lmstudio` | `openai-compatible`), so the
 *  panel can pre-select it the way the Visual Studio window does; null when nothing answered. */
export interface ConnectionCheckResult {
  ok: boolean;
  provider: string | null;
  /** Nothing detected, but a server refused the probe (a wrong API key: 401): the line to show instead of
   *  "unreachable", already in the user's language. */
  refused?: string | null;
}

/** One bubble, flattened for `chat/export`. `name` is the model (assistant) or tool name. */
export interface ChatExportMessage {
  role: string;
  name?: string;
  content: string;
  timestamp?: string;
  /** A notice (a slash command shown as typed): exported, not counted as a turn. */
  notice?: boolean;
}

/** `chat/export` — the adapter sends what it SHOWS, the Core renders the document. The whole
 *  point is that there is one exporter: the stats header and the .txt/.md layouts are the Core's,
 *  not something each editor writes again (and loses). */
export interface ChatExportParams {
  asPlainText: boolean;
  messages: ChatExportMessage[];
  sessionTokens?: number;
  durationSeconds?: number;
}

/** `command/list` entry — one slash command for the autocomplete popup. */
export interface SlashCommandInfo {
  command: string;
  hint: string;
}

/** `mention/categories` entry — one typed @mention category. */
export interface MentionCategory {
  token: string;
  description: string;
  queryBased: boolean;
}

/** One `mention/search` hit (file/folder sub-search). */
export interface MentionItem {
  label: string;
  detail: string;
  value: string;
}

/** `mention/resolve` answer: chip label + content (nulls = nothing to attach).
 * `notice` is a localized sentence to show INSTEAD of a chip — "nothing is paused", not a
 * failure. Without it an empty answer and a broken one both came out as silence. */
export interface MentionResolveResult {
  name: string | null;
  content: string | null;
  notice?: string | null;
}

/** One editor-side effect a handled slash command asks the adapter to apply.
 * Kinds: setPrompt | sendAsPrompt | attachChip (name = chip label) | copyToClipboard |
 * clearTranscript | stateChange (name = key) | openFile | exportRequest.
 * Unknown kinds must be ignored (forward compatibility). */
export interface SlashEffect {
  kind: string;
  value?: string | null;
  name?: string | null;
}

/** `command/slash` answer: `handled: false` ⇒ send the text as a normal chat prompt. */
export interface SlashCommandResult {
  handled: boolean;
  markdown?: string | null;
  effects?: SlashEffect[] | null;
}

/** `codeAction/run` — headless in-place code action over the active document. */
export interface CodeActionParams {
  kind: 'fix' | 'refactor' | 'doc';
  text: string;
  selStart: number;
  selEnd: number;
  model?: string;
  /** The document's path. `/doc` needs it for the semantic block (namespace, hierarchy,
   *  overrides, interface contracts) the Visual Studio window has always had: the extractor
   *  dispatches on the extension and reads neighbouring files to find the contracts. */
  path?: string;
}

/** One independently acceptable hunk: replace [start, end) of the submitted text. */
export interface CodeActionEdit {
  index: number;
  start: number;
  end: number;
  newText: string;
}

/** `codeAction/run` answer; `newText` is the full rewritten document when edited,
 * `failureDetail` the underlying error message when failed. */
export interface CodeActionResult {
  outcome: 'edited' | 'noChange' | 'failed' | 'cancelled';
  edits: CodeActionEdit[];
  newText?: string | null;
  failureDetail?: string | null;
}

/** `code/excerpt` answer: what a read-only action (/explain, /review) puts in the prompt — the code, or
 *  its first lines and a marker naming the count — and the label shown under the question. */
export interface CodeExcerptResult {
  text: string;
  label: string;
  truncated: boolean;
}

/** One prompt layer of the Context X-Ray panel (`xray/panel` / `xray/toggle`). */
export interface XRaySection {
  id: string;
  label: string;
  tokens: number;
  percent: number;
  content: string;
  enabled: boolean;
  canToggle: boolean;
}

/** Full X-Ray panel model, ready to render in the webview. */
export interface XRayPanel {
  sections: XRaySection[];
  totalTokens: number;
  historyTokens: number;
  contextWindow: number;
  fillPercent: number;
  overheadWarning: boolean;
  rawPrompt: string;
  /** Tool definitions the next turn carries — counted in fillPercent. */
  toolTokens: number;
}

export interface ApprovalNote {
  message: string;
  /** The card the chat draws; absent from an older host, which only sends the message. */
  card?: ApprovalCard | null;
}

/** One line of an approval card's preview. */
export interface ApprovalPreviewLine {
  kind: 'add' | 'del' | 'ctx' | 'gap';
  text: string;
}

/** An approval as a card (`ApprovalCard` in the Core): what will happen, to what, the start of the change. */
export interface ApprovalCard {
  title: string;
  subject: string;
  meta: string;
  preview: ApprovalPreviewLine[];
  more: string;
  alwaysTooltip: string;
  message: string;
}

/** One file a run changed. */
export interface RunFileLine {
  path: string;
  name: string;
  added: number;
  removed: number;
  created: boolean;
  gone: boolean;
}

/** A turn's run as the chat shows it under the answer (`RunSummary` in the Core). */
export interface RunSummary {
  steps: number;
  title: string;
  detail: string;
  files: RunFileLine[];
  check: 'none' | 'buildPassed' | 'buildFailed' | 'testsPassed' | 'testsFailed';
  checkText: string;
  runId: string;
}

export interface SavedMessage {
  role: string;
  content: string;
  toolName?: string | null;
  timestamp?: string | null;
}

export interface SessionSummary {
  name: string;
  savedAt: string;
  messageCount: number;
  preview: string;
  /** Set on branches (`/branch`): the session this one was forked from, and where. */
  parent?: string | null;
  forkTurn?: number | null;
}

/** `session/list` answer: the sessions that could be read, and the files that could not. */
export interface SessionListResult {
  sessions: SessionSummary[];
  /** Session files present but unreadable: absent from `sessions`, their names still TAKEN. */
  unreadable: string[];
  /** Localized sentence naming them, or null when every file was read. */
  notice?: string | null;
}

export interface SessionLoadResult {
  name: string;
  messages: SavedMessage[];
  /** What the next question will send: the context gauge's fill for the conversation just loaded. */
  nextTurnTokens?: number;
}

/** session/branch answer: the new branch, its parent and the truncated transcript to render. */
export interface SessionBranchResult {
  name: string;
  parent: string;
  forkTurn: number;
  messages: SavedMessage[];
  /** Localized confirmation bubble, built host-side from the shared .resx. */
  message: string;
  /** What the next question will send: the context gauge's fill for the branch. */
  nextTurnTokens?: number;
}

/**
 * session/branchCommand answer: `/branch [args]` decided on the adapter's transcript. Exactly one
 * outcome; for a switch, `message` is the confirmation to show once the load succeeded.
 */
export interface SessionBranchCommandResult {
  message?: string | null;
  forkTurn?: number | null;
  switchTo?: string | null;
}

/** session/title answer: the bare LLM title plus the timestamped save file name. */
export interface SessionTitleResult {
  title: string;
  fileName: string;
}

/** approval/request answer: 0 = deny, 1 = allow once, 2 = always allow (session). */
export const enum ApprovalAnswer {
  Deny = 0,
  Once = 1,
  Always = 2,
}

/** One choice of a select field (texts are product names — never localized). */
export interface SettingsOption {
  value: string;
  text: string;
  /** The sentence under the option's name when it is drawn as a card. */
  description?: string | null;
}

/** An editable setting. `label`/`hint` are resource names resolved via `settings/strings`. */
export interface SettingsField {
  key: string;
  kind: 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';
  label: string;
  hint?: string | null;
  unit?: string | null;
  button?: string | null;
  options?: SettingsOption[] | null;
  /** Factory value of a numeric field (clearing the box restores it, as in the VS window), and of a
   *  field that opens the advanced fold ("true"/"false" for a boolean). */
  defaultValue?: string | null;
  /** A check box showing the opposite of the stored boolean. */
  inverted?: boolean;
  /** A value other than `defaultValue` opens the page with its advanced sections shown. */
  opensFold?: boolean;
  /** Resource name of the hint shown instead when the selected server is not Ollama. */
  hintNotOllama?: string | null;
  /** The structured editor drawn over a list setting's text. */
  editor?: 'pinnedFiles' | 'mcpServers' | 'approvalRules' | 'nameValue' | 'cards' | 'segmented' | null;
  /** A numeric box whose 0 means "not set": shown empty, and empty saves 0. */
  zeroIsEmpty?: boolean;
  /** Resource name of what an optional model list shows for its empty value ("Same as chat"). */
  emptyChoice?: string | null;
  /** Resource names of a nameValue table's column headers. */
  columns?: string[] | null;
  /** Bounds of a numeric box (declared once in the Core schema): a value outside them is named, never saved. */
  min?: number | null;
  max?: number | null;
}

/** `settings/indexCard`: the code index as the Code search page shows it. */
export interface IndexCard {
  state: 'noWorkspace' | 'indexing' | 'failed' | 'stopped' | 'notBuilt' | 'ready';
  title: string;
  detail: string;
  notes: string[];
  oversizeNote: string;
  oversizeFiles: string[];
  modelLine: string;
  buttonLabel: string;
  canRebuild: boolean;
}

/** `settings/loadedModels` and `settings/unloadModels`: what the server holds in memory, what Inferpal uses each model
 *  for, and what the last unload did. */
export interface LoadedModels {
  summary: string;
  rows: { name: string; uses: string; details: string }[];
  canUnload: boolean;
  message?: string | null;
}

/** `settings/suggestModels`: the best installed models, proposed into the form — never saved by the host. */
export interface ModelSuggestion {
  fields: { key: string; value: string; reason: string; changed: boolean }[];
  notes: string[];
  refusal?: string | null;
  summary: string;
}

/** `settings/exclusions`: the patterns of .inferpal/project.json the index applies. */
export interface SettingsExclusions {
  patterns: string[];
  file: string;
  exists: boolean;
}

/** One @Docs site of the Code search page. */
export interface DocsSiteRow {
  id: string;
  title: string;
  address: string;
  state: 'indexing' | 'notIndexed' | 'partial' | 'indexed';
  status: string;
  holeNote: string;
  busy: boolean;
  removeLabel: string;
}

/** `settings/docsSites` and `settings/docsAction`: the sites, and what the command said. */
export interface SettingsDocs {
  sites: DocsSiteRow[];
  message?: string | null;
}

/** One share of the conversation's window. */
export interface ContextUsagePart {
  key: 'instructions' | 'tools' | 'conversation';
  label: string;
  amount: string;
  percent: number;
}

/** `settings/contextUsage`: how full the conversation's window is, and with what. */
export interface ContextUsage {
  summary: string;
  parts: ContextUsagePart[];
}

/** One of the project's files the prompt reads (`settings/projectFiles`). */
export interface ProjectFileRow {
  name: string;
  description: string;
  fullPath: string;
  exists: boolean;
}

/** What one pinned file costs the prompt (`settings/pinSizes`). */
export interface PinnedFileSize {
  path: string;
  size: string;
  missing: boolean;
}

/** One MCP server card (`mcp/cards`, `mcp/retry`, `mcp/authorize`). */
export interface McpCard {
  name: string;
  transport: string;
  target: string;
  enabled: boolean;
  state: 'connected' | 'signIn' | 'failed' | 'off' | 'notStarted';
  statusText: string;
  cause?: string | null;
  toolCount: number;
}

export interface McpCardsResult {
  summary: string;
  cards: McpCard[];
  /** Why a sign-in failed. */
  error?: string | null;
}

/** One row of the approval rules table (`permissions/table`). */
export interface ApprovalRuleRow {
  source: 'team' | 'machine';
  allow?: boolean | null;
  tool: string;
  pattern: string;
  status: 'inForce' | 'ignoredAllow' | 'unreadable';
  effectText: string;
  fromText: string;
  noteText?: string | null;
  /** Index of the line in the machine setting; -1 for a team rule. */
  machineLine: number;
}

export interface ApprovalRuleTable {
  teamUnusable: boolean;
  rows: ApprovalRuleRow[];
}

export interface SettingsSection {
  /** Resource name of the heading; empty = no heading. */
  title: string;
  fields: SettingsField[];
  description?: string | null;
  /** `advanced`: shown only when the page's "Show advanced settings" box is checked. */
  gate?: string | null;
  /** Rendered folded under its title. */
  collapsible?: boolean;
  /** Resource name of a sentence under the fields. */
  note?: string | null;
  /** Fields in two columns. */
  grid?: boolean;
  /** A live summary under the fields: `approvalRules`. */
  widget?: string | null;
}

/** One page of the settings window, reached from the side navigation. */
export interface SettingsTab {
  key: string;
  title: string;
  description: string;
  sections: SettingsSection[];
  /** Resource name of the page's "Show advanced settings" box. */
  advancedToggle?: string | null;
}

/** `settings/schema` answer: the form the webview renders, declared once in the Core. */
export interface SettingsSchema {
  tabs: SettingsTab[];
}

// ── Reverse `debug/*` DTOs (host → editor) ───────────────────────────────────
// Mirror of Inferpal.Host/HostProtocol.cs. Deliberately not a rendering of the Debug Adapter
// Protocol: only what the Core's IDebugSession port needs crosses this wire, and values stay
// opaque strings because the two editors' debuggers render them differently on purpose.

export interface DebugBreakpointDto {
  file: string;
  line: number;
  enabled: boolean;
}

export interface DebugBreakpointParams {
  file: string;
  line: number;
}

export interface DebugStepParams {
  /** `over` | `into` | `out`. */
  kind: string;
}

export interface DebugEvaluateParams {
  expression: string;
  /** Null = the top frame of the current stop. */
  frameId?: number | null;
}

export interface DebugFrameDto {
  id: number;
  function: string;
  file: string | null;
  line: number | null;
}

export interface DebugVariableDto {
  name: string;
  type: string;
  /** Rendered by the debug adapter. Present it; never parse it. */
  value: string;
}

export interface DebugStopStateDto {
  reason: string;
  threadId: number;
  frames: DebugFrameDto[];
  locals: DebugVariableDto[];
  exception?: string | null;
  /**
   * Id of the frame `locals` were read from. The ordinary capture reads the top frame, the `/tdd`
   * capture reads the first frame under the workspace root, and the renderer hides frames outside
   * it — so the frame printed first is often not this one, and the reader must be told which.
   */
  localsFrameId?: number | null;
}

/**
 * `debug/start` answer — four outcomes: a stop, a completed run (all null), a failure, or a program
 * still running with no stop within the budget (`stillRunning`) — which is neither of the other two.
 */
export interface DebugStartDto {
  state: DebugStopStateDto | null;
  failure: string | null;
  stillRunning?: boolean;
}

/**
 * `debug/continue` and `debug/step` answer: the stop, or why there is none. `outcome` is `ended` (the run finished:
 * the assistant's breakpoints go with the session), `running` (still running past the budget: they stay) or
 * `not-paused`; `failure` is the editor's own words when the resume itself failed. The outcome strings are the host's
 * (`DebugOps.Resumed`).
 */
export interface DebugResumeDto {
  state: DebugStopStateDto | null;
  outcome: 'ended' | 'running' | 'not-paused' | null;
  failure?: string;
}

/** `debug/captureTest`: launch the repro runner under coreclr and capture the failure. */
export interface DebugCaptureTestParams {
  program: string;
  args: string[];
  cwd: string;
  projectRoot: string;
}

/** `config/update` answer: what the save could not use. */
/** `pins/list|add|remove` — the pinned files after the call, and what the host had to say. */
export interface PinsResult {
  pins: string[];
  notice?: string | null;
}

export interface ConfigUpdateResult {
  /** Permission rules the product could not read — the field is saved, these lines are inert. */
  permissionRulesIgnored: number;
}
