// Typed postMessage protocol between the extension host (chatViewProvider.ts) and the
// chat webview (src/webview/main.ts). Both sides are bundled from this repo, so the
// types are shared instead of mirrored. No 'vscode' import — the webview bundle uses it.
import type {
  ApprovalCard, CutCard, ImportCard, InputCard, InputFormAnswer, ModeInfo, ModeLabels, ModelWait, RewindCard, RunReview, RunSummary,
  SavedMessage, SetupPlace,
  SetupPlan,
  XRayPanel,
} from './protocol';

/**
 * The first-run card, ready to draw: the extension runs the steps (`setupFlow.ts`), the webview only renders what it is
 * sent. `searching`: the places being looked at; `found`: a server answers, with its proposal; `nothing`: what each place
 * answered, and an address to type; `noModel`: a server without a chat model, with what to download.
 */
export interface WvSetupView {
  phase: 'searching' | 'found' | 'nothing' | 'noModel';
  /** The card's fixed labels, from the host (`setup/strings`). */
  strings: Record<string, string>;
  places: SetupPlace[];
  /** The server the card shows (found, noModel). */
  server?: { url: string; provider: string } | null;
  plan?: SetupPlan | null;
  /** Other servers that answer too (found). */
  others: SetupPlace[];
  /** A download in progress. */
  pull?: { title: string; status: string; percent?: number | null } | null;
  /** An action is running: the card's buttons wait. */
  busy?: boolean;
  /** What failed, said on the card. */
  error?: string | null;
}

/** A gesture on the first-run card. */
export type WvSetupAction =
  | { action: 'lookAgain' }
  | { action: 'connect'; url: string; key?: string }
  | { action: 'use'; url: string; provider: string }
  | { action: 'start' }
  | { action: 'chooseOther' }
  | { action: 'download'; tag: string }
  /** The server's graphics memory, chosen on the card. */
  | { action: 'card'; gb: number };

/** One rendered transcript entry (the extension host owns the list). */
export interface WvTranscriptItem {
  role: 'user' | 'assistant' | 'status' | 'tool' | 'error' | 'input';
  /** Message text; for 'tool' entries this is the tool name; for 'input' entries, the receipt an answered input card
   *  left (`InputReceipt.Saved` in the Core: its title, its detail, then what was sent). */
  text: string;
  toolInput?: string;
  toolOutput?: string;
  /** A writing call's change, in the approval card's lines (`ToolNotice.diff`). */
  toolDiff?: string;
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
  /** The run of changes the turn made, read back from a saved session (which keeps the id, not the summary). */
  runId?: string;
  /** A saved message of a role this window does not draw (Visual Studio's plan card): shown as a notice, written
   *  back exactly as it was read. */
  savedAs?: SavedMessage;
  /** Screen only, never saved: this end notice offers Continue (ChatTurnPolicy.OffersContinue). Taken off when the next
   *  turn starts; kept across a redraw of the view, which rebuilds it from the transcript. */
  continues?: boolean;
  /** Screen only, never saved: a 'status' entry that is the line where a mode starts, drawn as a rule. */
  modeLine?: boolean;
  /** Screen only, never saved: under this error, a turn cut at its bound — kept across a redraw of the view. */
  cut?: CutCard | null;
  /** The `/import` preview this notice is: drawn as a card with a box per item instead of its text (the text, its
   *  Markdown, is what a saved conversation keeps). */
  importCard?: WvImportCard;
  /** A 'user' entry that is a NOTE typed while a run worked (`chat/steer`), not a question: `waiting` until the model
   *  reads it at the run's next step, `read` after. Saved with the Core's `SessionManager.NoteMarker`, folded into its
   *  question by the restore — never a question of its own. */
  note?: 'waiting' | 'read';
  /** The note's id in `chat/steer` and `chat/noteApplied`. */
  noteId?: string;
  /** The tool calls the run had made when the model read the note. */
  noteAfter?: number;
}

/** An `/import` card in the transcript: the host's card, the id its Apply answers with, and whether it was used. */
export interface WvImportCard extends ImportCard {
  id: string;
  applied?: boolean;
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
  /** The machines other than this one the code is sent to (see `BackendStatusResult.offMachine`). */
  offMachine?: string[] | null;
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

/** The banner of a build task that failed: the errors of the Problems panel, the first one, the task. */
export interface WvBuildBanner {
  /** Which failed build: a banner the person closed is not drawn again for the same one. */
  seq: number;
  errors: number;
  /** `path(line,column): error CODE: message` — the first error by file and line; null when the panel has none. */
  firstError: string | null;
  task: string;
}

/** One context chip in the composer (attachment pending for the next turn). */
export interface WvChip {
  name: string;
  /** Found by the search while the person types, not attached by them: drawn apart, said to be excerpts. */
  auto?: boolean;
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
  /** The messages waiting to be sent after the turn, in order. */
  queue: string[];
}

export type ExtToWebview =
  | WvHydrate
  | { type: 'turnStarted'; prompt: string; timestamp: string; history: string[] }
  | { type: 'token'; text: string }
  /** `text` = the throttled reasoning tail (agent path, already prefixed with its emoji); absent
   *  in plain chat, where the webview falls back to its generic indicator. */
  | { type: 'thinking'; text?: string | null }
  | { type: 'status'; text: string }
  /** The turn's wait card: a request waiting on a server that reads its prompt; null takes it away. */
  | { type: 'wait'; wait: ModelWait | null }
  | { type: 'assistant'; text: string; timestamp: string; notice?: boolean }
  | { type: 'tool'; name: string; input: string; output: string; hasErrors: boolean; diff?: string; timestamp: string; expanded: boolean }
  | { type: 'plan'; plan: WvPlan }
  | { type: 'approval'; id: number; message: string; card?: ApprovalCard | null }
  | { type: 'approvalDismiss'; id: number }
  /** An MCP server's form to fill; the same `card.id` again replaces the card drawn (what to fix). */
  | { type: 'inputForm'; card: InputCard }
  /** The input card's turn stopped, or a note set it aside: the card goes, its line follows. */
  | { type: 'inputDismiss'; id: string }
  /** The line an answered card leaves where it was (`InputReceipt.Saved` in the Core). */
  | { type: 'inputReceipt'; id: string; saved: string }
  | { type: 'mentionSuggestions'; items: string[]; query: string }
  | { type: 'xrayPanel'; panel: XRayPanel }
  /** A run's review card, or the card refreshed after a file's action; null: the run is no longer kept; absent: an
   * action failed with no card to draw — the card shown takes its actions back, under the message. */
  | { type: 'runReview'; runId: string; review?: RunReview | null; message?: string }
  | { type: 'streamReset' }
  // notice: the turn's text is a notice no answer of the model (a slash command, a code action's outcome) — drawn as the
  // transcript keeps it, under no model's name.
  | { type: 'turnEnded'; text: string; error: string | null; cancelled: boolean; tokens: number; promptTokens: number; timestamp: string; endNotice?: string | null; offersContinue?: boolean; contextWindow?: number; run?: RunSummary | null; duration?: string | null; model?: string | null; canRegenerate?: boolean; notice?: boolean; cut?: CutCard | null }
  | { type: 'editorContext'; file: string | null; problems: number }
  /** A build task failed (the banner), or passed (null: the banner goes). */
  | { type: 'buildBanner'; banner: WvBuildBanner | null }
  | { type: 'planMode'; enabled: boolean }
  /** The modes found for the workspace, the one held (`null`: Chat, Agent or Plan), the selector's words. */
  | { type: 'modes'; modes: ModeInfo[]; current: string | null; labels: ModeLabels | null }
  /** Where a mode starts in the thread: its line, drawn as a rule. */
  | { type: 'modeLine'; text: string }
  | { type: 'backendStatus'; status: WvBackendStatus }
  | { type: 'agentMode'; enabled: boolean }
  | { type: 'density'; compact: boolean }
  | { type: 'setPrompt'; text: string }
  /** The question gone back to: in the message box, above what was typed there. */
  | { type: 'putBackQuestion'; text: string }
  /** The card under the `ordinal`-th question: a plan, a refusal, or (`card` absent) its actions back under `message`. */
  | { type: 'rewindCard'; ordinal: number; card?: RewindCard | null; message?: string }
  /** `category` and `query` name the request answered: only the latest one is shown. */
  | { type: 'mentionResults'; category: string; query: string; items: WvMentionItem[] }
  | { type: 'chips'; chips: WvChip[] }
  | { type: 'pins'; pins: string[] }
  | { type: 'stepPaused' }
  | { type: 'stepResumed' }
  /** A note typed during the run, drawn in the turn; `read` once the model read it, after `afterCalls` tool calls. */
  | { type: 'note'; id: string; text: string; state: 'waiting' | 'read'; afterCalls?: number }
  /** A note no step read: off the turn (sent next, or back in the input box). */
  | { type: 'noteRemoved'; id: string }
  /** The messages waiting to be sent after the turn, in order. */
  | { type: 'queue'; items: string[] }
  /** Text given back to the input box, in front of what is typed there. */
  | { type: 'restoreDraft'; text: string }
  /** The model picker's list, re-read from the server when the menu opened; `model`: the window's model, when its
   *  setting changed outside the picker. */
  | { type: 'models'; models: string[]; model?: string }
  /** The slash commands, re-read when a command starts being typed. */
  | { type: 'commands'; commands: WvSlashCommand[] }
  /** The first-run card, or null to take it away. */
  | { type: 'setup'; view: WvSetupView | null };

export type WebviewToExt =
  | { type: 'ready' }
  /** `typed`: the composer's text, which the webview cleared as it sent — a refusal gives it back. */
  | { type: 'send'; text: string; typed?: boolean }
  /** Enter during a turn: a note for the run's next step (sent after the turn when no step will read it). */
  | { type: 'steer'; text: string }
  /** Ctrl+Enter during a turn: sent as the next message once the turn ends. */
  | { type: 'queue'; text: string }
  /** A queued message taken out of the queue. */
  | { type: 'unqueue'; index: number }
  /** The welcome banner's "Fix them": the Problems panel attached, then `text` sent. */
  | { type: 'fixProblems'; text: string }
  /** The build banner's "Fix them": as `fixProblems`, and the banner goes once the errors are sent. */
  | { type: 'fixBuild'; text: string }
  /** The build banner's "Show the errors": the Problems panel. */
  | { type: 'showProblems' }
  /** The build banner closed: it is not drawn again until the next build fails. */
  | { type: 'dismissBuildBanner' }
  | { type: 'cancel' }
  | { type: 'reset' }
  | { type: 'pickModel'; model: string }
  | { type: 'approvalAnswer'; id: number; answer: number }
  | { type: 'inputAnswer'; id: string; action: InputFormAnswer['action']; values: InputFormAnswer['values'] }
  | { type: 'mentionQuery'; query: string }
  | { type: 'openApprovalDiff'; text: string }
  | { type: 'xrayToggle'; id: string; enabled: boolean }
  /** A question's "Go back here" (the `ordinal`-th question bubble of the view, notices included). */
  | { type: 'rewindPlan'; ordinal: number; question: string }
  | { type: 'rewind'; ordinal: number; question: string; conversationOnly: boolean; expectedRuns: string[] }
  /** The result bar's "Review changes". */
  | { type: 'runReview'; runId: string }
  /** One file of a run's review undone, redone or kept — or, with `piece` (1-based), one change of it. */
  | { type: 'runFile'; runId: string; path: string; action: 'undo' | 'redo' | 'keep'; piece?: number }
  | { type: 'copyText'; text: string }
  | { type: 'regenerate' }
  /** A cut turn's "Open time limits": the settings panel, on the page that holds them. */
  | { type: 'openSettingsPage'; page: string; section: string }
  | { type: 'continue' }
  | { type: 'retryConnection' }
  /** The model picker opened: re-read the server's list. */
  | { type: 'listModels' }
  /** A slash command starts being typed: re-read the commands (prompt files and templates change). */
  | { type: 'listCommands' }
  | { type: 'openXray' }
  | { type: 'mentionSearch'; category: string; query: string }
  | { type: 'resolveMention'; category: string; value?: string }
  | { type: 'removeChip'; index: number }
  /** The draft changed: the extension looks for the code it is about once typing pauses. */
  | { type: 'draft'; text: string }
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
  /** A mode found for the workspace, picked in the selector. */
  | { type: 'setFoundMode'; name: string }
  /** The selector's list opens: the modes are read again. */
  | { type: 'modesOpen' }
  /** A file of a run's result bar. */
  | { type: 'openFile'; path: string }
  /** An `/import` card's Apply: the numbers of the boxes left ticked (none is a choice too). */
  | { type: 'importApply'; id: string; numbers: number[] }
  /** A gesture on the first-run card. */
  | ({ type: 'setupAction' } & WvSetupAction);
