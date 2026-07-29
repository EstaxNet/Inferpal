// Typed postMessage protocol between the extension host (chatViewProvider.ts) and the
// chat webview (src/webview/main.ts). Both sides are bundled from this repo, so the
// types are shared instead of mirrored. No 'vscode' import — the webview bundle uses it.
import type { XRayPanel } from './protocol';

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
}

/** Header connection badge (mirror of `backend/status`). */
export interface WvBackendStatus {
  connected: boolean;
  vramBadge: string;
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
  mentionCategories: WvMentionCategory[];
  chips: WvChip[];
}

export type ExtToWebview =
  | WvHydrate
  | { type: 'turnStarted'; prompt: string; timestamp: string; history: string[] }
  | { type: 'token'; text: string }
  | { type: 'thinking' }
  | { type: 'status'; text: string }
  | { type: 'tool'; name: string; input: string; output: string; hasErrors: boolean; timestamp: string; expanded: boolean }
  | { type: 'plan'; plan: WvPlan }
  | { type: 'approval'; id: number; message: string }
  | { type: 'mentionSuggestions'; items: string[] }
  | { type: 'xrayPanel'; panel: XRayPanel }
  | { type: 'streamReset' }
  | { type: 'turnEnded'; text: string; error: string | null; cancelled: boolean; tokens: number; promptTokens: number; timestamp: string }
  | { type: 'backendStatus'; status: WvBackendStatus }
  | { type: 'agentMode'; enabled: boolean }
  | { type: 'setPrompt'; text: string }
  | { type: 'mentionResults'; category: string; items: WvMentionItem[] }
  | { type: 'chips'; chips: WvChip[] };

export type WebviewToExt =
  | { type: 'ready' }
  | { type: 'send'; text: string }
  | { type: 'cancel' }
  | { type: 'reset' }
  | { type: 'pickModel'; model: string }
  | { type: 'approvalAnswer'; id: number; answer: number }
  | { type: 'mentionQuery'; query: string }
  | { type: 'openApprovalDiff'; text: string }
  | { type: 'xrayToggle'; id: string; enabled: boolean }
  | { type: 'copyText'; text: string }
  | { type: 'regenerate' }
  | { type: 'toggleAgentMode' }
  | { type: 'retryConnection' }
  | { type: 'openXray' }
  | { type: 'mentionSearch'; category: string; query: string }
  | { type: 'resolveMention'; category: string; value?: string }
  | { type: 'removeChip'; index: number }
  | { type: 'attachActive' }
  | { type: 'attachSelection' }
  | { type: 'attachBrowse' };
