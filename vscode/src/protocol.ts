// Wire DTOs of the editor ⇄ Inferpal.Host JSON-RPC protocol.
// Mirror of Inferpal.Host/HostProtocol.cs — camelCase on the wire both ways.

export interface InitializeParams {
  rootDir: string;
  locale?: string;
  clientName?: string;
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

/** `command/slash` answer: `handled: false` ⇒ send the text as a normal chat prompt. */
export interface SlashCommandResult {
  handled: boolean;
  markdown?: string | null;
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
}

export interface SessionLoadResult {
  name: string;
  messages: SavedMessage[];
}

/** approval/request answer: 0 = deny, 1 = allow once, 2 = always allow (session). */
export const enum ApprovalAnswer {
  Deny = 0,
  Once = 1,
  Always = 2,
}
