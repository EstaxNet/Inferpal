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

export interface ChatSendParams {
  prompt: string;
  model?: string;
  agentMode?: boolean;
}

export interface ChatSendResult {
  text: string;
  cancelled: boolean;
  tokensUsed: number;
  promptTokens: number;
  error?: string | null;
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

export interface DocumentParams {
  path: string;
  text?: string;
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

/** `mention/resolve` answer: chip label + content (nulls = nothing to attach). */
export interface MentionResolveResult {
  name: string | null;
  content: string | null;
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
  outcome: 'edited' | 'noChange' | 'failed';
  edits: CodeActionEdit[];
  newText?: string | null;
  failureDetail?: string | null;
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
}

export interface ApprovalNote {
  message: string;
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

export interface SessionLoadResult {
  name: string;
  messages: SavedMessage[];
}

/** session/branch answer: the new branch, its parent and the truncated transcript to render. */
export interface SessionBranchResult {
  name: string;
  parent: string;
  forkTurn: number;
  messages: SavedMessage[];
  /** Localized confirmation bubble, built host-side from the shared .resx. */
  message: string;
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
}

/** An editable setting. `label`/`hint` are resource names resolved via `settings/strings`. */
export interface SettingsField {
  key: string;
  kind: 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';
  label: string;
  hint?: string | null;
  unit?: string | null;
  gate?: string | null;
  button?: string | null;
  options?: SettingsOption[] | null;
}

export interface SettingsSection {
  title: string;
  fields: SettingsField[];
  toggleGate?: string | null;
  toggleLabel?: string | null;
  toggleHint?: string | null;
}

export interface SettingsTab {
  key: string;
  title: string;
  sections: SettingsSection[];
}

/** `settings/schema` answer: the form the webview renders, declared once in the Core. */
export interface SettingsSchema {
  tabs: SettingsTab[];
  headerFields: SettingsField[];
}

// ── Reverse `debug/*` DTOs (host → editor), roadmap §21 ──────────────────────
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
}

/** `debug/start` answer — three outcomes: a stop, a completed run (both null), or a failure. */
export interface DebugStartDto {
  state: DebugStopStateDto | null;
  failure: string | null;
}
