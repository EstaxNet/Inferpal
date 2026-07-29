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

/** `command/slash` answer: `handled: false` ⇒ send the text as a normal chat prompt. */
export interface SlashCommandResult {
  handled: boolean;
  markdown?: string | null;
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
