// Typed postMessage protocol between the extension host (chatViewProvider.ts) and the
// chat webview (src/webview/main.ts). Both sides are bundled from this repo, so the
// types are shared instead of mirrored. No 'vscode' import — the webview bundle uses it.
import type { ApprovalCard, RunSummary, SavedMessage, XRayPanel } from './protocol';

/** One rendered transcript entry (the extension host owns the list). */
export interface WvTranscriptItem {
  role: 'user' | 'assistant' | 'status' | 'tool' | 'error';
  /** Message text; for 'tool' entries this is the tool name. */
  text: string;
  toolInput?: string;
  toolOutput?: string;
  hasErrors?: boolean;
  /** Pre-formatted local time (HH:MM), stamped when the entry was pushed. */
  timestamp?: string;
  /** An 'assistant' entry that is a NOTICE (an end notice, a slash command's output, a failed save), not an answer
   *  the model gave — or a 'user' entry that is a slash command served without the model, not a question: same bubble,
   *  but a restored conversation must not hand it back to the model as one. */
  notice?: boolean;
  /** An answer's model, duration and run, kept for this session's redraws (a saved session does not carry them). */
  model?: string;
  duration?: string;
  run?: RunSummary | null;
  /** A saved message of a role this window does not draw (Visual Studio's plan card): shown as a notice, written
   *  back exactly as it was read. */
  savedAs?: SavedMessage;
}

/** Header connection badge (mirror of `backend/status`). */
export interface WvBackendStatus {
  connected: boolean;
  vramBadge: string;
  /** The server refused the check (see `BackendStatusResult.refused`). */
  refused?: string | null;
  /** The HOST is not running (no folder open, a start that failed): the badge text naming its remedy, already
   *  localized. The backend was not asked — "unreachable" would send the user to check a server that is fine. */
  hostDown?: string | null;
  /** The configured backend, for the header ("LM Studio"). */
  server?: string | null;
}

/** One slash command of the autocomplete popup. */
export interface WvSlashCommand {
  command: string;
  hint: string;
}

/** One typed @mention category (host-localized description). */
export interface WvMentionCategory {
  token: string;
  description: string;
  queryBased: boolean;
}

/** One @file/@folder sub-search hit. */
export interface WvMentionItem {
  label: string;
  detail: string;
  value: string;
}

/** One context chip in the composer (attachment pending for the next turn). */
export interface WvChip {
  name: string;
}

export interface WvPlanStep {
  text: string;
  /** Free-form status from the agent loop ('pending' | 'running' | 'done' | …). */
  status: string;
}

/** Agent plan block, re-pushed whole on every step update. */
export interface WvPlan {
  goal: string;
  steps: WvPlanStep[];
}

/** Everything the webview needs to rebuild itself from scratch (it can be destroyed on hide). */
export interface WvHydrate {
  type: 'hydrate';
  transcript: WvTranscriptItem[];
  /** A question meant for the model is in the thread: Regenerate is offered only then. */
  canRegenerate?: boolean;
  models: string[];
  model: string;
  busy: boolean;
  stream: string;
  status: WvBackendStatus | null;
  commands: WvSlashCommand[];
  agentMode: boolean;
  /** Configured context window size (0 = unknown → gauge hidden). */
  contextWindow: number;
  /** Prompt tokens of the last turn (feeds the context gauge). */
  promptTokens: number;
  /** Completion tokens of the last turn (token info line). */
  lastTokens: number;
  plan: WvPlan | null;
  /** Prompt history, most-recent-last (Shift+↑↓ navigation is webview-local). */
  history: string[];
  /** Default expansion of tool bubbles (host config ToolBubblesExpanded). */
  toolBubblesExpanded: boolean;
  /** The conversation drawn compact (host config ChatDensity). */
  compact: boolean;
  mentionCategories: WvMentionCategory[];
  chips: WvChip[];
  /** Files pinned into every request (full paths). */
  pins: string[];
  /** Read-only plan mode (the Plan segment of the mode switch). */
  planMode: boolean;
  /** The file open in the editor, by name, for the welcome screen's cards; null when none. */
  editorFile: string | null;
  /** Errors in the Problems panel, for the welcome screen's banner. */
  problems: number;
  /** The line naming a question's attachments ("📎 Attached: {0}"), so its names can be drawn as chips. */
  attachedRecap: string;
}

export type ExtToWebview =
  | WvHydrate
  | { type: 'turnStarted'; prompt: string; timestamp: string; history: string[] }
  | { type: 'token'; text: string }
  /** `text` = the throttled reasoning tail (agent path, already prefixed with its emoji); absent
   *  in plain chat, where the webview falls back to its generic indicator. */
  | { type: 'thinking'; text?: string | null }
  | { type: 'status'; text: string }
  | { type: 'assistant'; text: string; timestamp: string }
  | { type: 'tool'; name: string; input: string; output: string; hasErrors: boolean; timestamp: string; expanded: boolean }
  | { type: 'plan'; plan: WvPlan }
  | { type: 'approval'; id: number; message: string; card?: ApprovalCard | null }
  | { type: 'approvalDismiss'; id: number }
  | { type: 'mentionSuggestions'; items: string[]; query: string }
  | { type: 'xrayPanel'; panel: XRayPanel }
  | { type: 'streamReset' }
  | { type: 'turnEnded'; text: string; error: string | null; cancelled: boolean; tokens: number; promptTokens: number; timestamp: string; endNotice?: string | null; contextWindow?: number; run?: RunSummary | null; duration?: string | null; model?: string | null; canRegenerate?: boolean }
  | { type: 'editorContext'; file: string | null; problems: number }
  | { type: 'planMode'; enabled: boolean }
  | { type: 'backendStatus'; status: WvBackendStatus }
  | { type: 'agentMode'; enabled: boolean }
  | { type: 'density'; compact: boolean }
  | { type: 'setPrompt'; text: string }
  /** `category` and `query` name the request answered: only the latest one is shown. */
  | { type: 'mentionResults'; category: string; query: string; items: WvMentionItem[] }
  | { type: 'chips'; chips: WvChip[] }
  | { type: 'pins'; pins: string[] }
  | { type: 'stepPaused' }
  | { type: 'stepResumed' }
  /** The model picker's list, re-read from the server when the menu opened; `model`: the window's model, when its
   *  setting changed outside the picker. */
  | { type: 'models'; models: string[]; model?: string }
  /** The slash commands, re-read when a command starts being typed. */
  | { type: 'commands'; commands: WvSlashCommand[] };

export type WebviewToExt =
  | { type: 'ready' }
  | { type: 'send'; text: string }
  /** The welcome banner's "Fix them": the Problems panel attached, then `text` sent. */
  | { type: 'fixProblems'; text: string }
  | { type: 'cancel' }
  | { type: 'reset' }
  | { type: 'pickModel'; model: string }
  | { type: 'approvalAnswer'; id: number; answer: number }
  | { type: 'mentionQuery'; query: string }
  | { type: 'openApprovalDiff'; text: string }
  | { type: 'xrayToggle'; id: string; enabled: boolean }
  | { type: 'copyText'; text: string }
  | { type: 'regenerate' }
  | { type: 'retryConnection' }
  /** The model picker opened: re-read the server's list. */
  | { type: 'listModels' }
  /** A slash command starts being typed: re-read the commands (prompt files and templates change). */
  | { type: 'listCommands' }
  | { type: 'openXray' }
  | { type: 'mentionSearch'; category: string; query: string }
  | { type: 'resolveMention'; category: string; value?: string }
  | { type: 'removeChip'; index: number }
  | { type: 'pinActive' }
  | { type: 'unpin'; path: string }
  | { type: 'attachActive' }
  | { type: 'attachSelection' }
  | { type: 'attachBrowse' }
  | { type: 'resumeStep' }
  /** The header's buttons: the saved conversations, and the "More" menu. */
  | { type: 'openSessions' }
  | { type: 'menu'; action: 'export' | 'settings' }
  /** The composer's mode switch: chat (no tools), agent, or read-only plan. */
  | { type: 'setMode'; mode: 'chat' | 'agent' | 'plan' }
  /** A file of a run's result bar. */
  | { type: 'openFile'; path: string };
