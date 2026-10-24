// Sidebar chat: WebviewViewProvider + typed postMessage protocol. The extension host
// (this class) is the source of truth for the transcript — the webview can be destroyed
// on hide and is re-hydrated on every resolveWebviewView.
import * as vscode from 'vscode';
import { t } from './i18n';
import type { CancellationToken } from 'vscode-jsonrpc';
import { HostClient } from './hostClient';
import { hostErrorText, hostUnavailableMessage, isConnectionLost, promptOpenFolder } from './hostStatus';
import { mentionTokens, resolveMention } from './mentionPaths';
import { followModelRouterSettings } from './modelRouterSettings';
import { SettingsPanel } from './settingsPanel';
import { SetupFlow } from './setupFlow';
import { buildBannerOf, isBuildTask, openTabPaths, panelErrors } from './editorBridge';
import { capAttachment, CappedText } from './attachmentCap';
import { failureText, testFilter } from './testGestures';
import { gitApi, repositoryFor } from './gitGestures';
import { renderChatHtml } from './webview/chatWebviewHtml';
import { samePath } from './debugBridge';
import { pickSession, toSavedMessages, toTranscript } from './chatSessions';
import {
  ApprovalCard, CodeActionResult, InputCard, InputFormAnswer, ProblemEntry, GestureQuestion, CommitMessageProposal,
  SavedMessage, SlashEffect, TestsGenerateResult, TurnNote,
} from './protocol';
import {
  WebviewToExt,
  WvBackendStatus,
  WvBuildBanner,
  WvMentionCategory,
  WvPlan,
  WvSlashCommand,
  WvTranscriptItem,
} from './webviewMessages';

/**
 * The folders the @ picker never searches: the Core's `WorkspaceScan.ExcludedDirNames`, which Visual Studio's picker
 * reads — Inferpal's own `.inferpal/history` snapshots and Python environments included — but `packages`, excluded
 * there only when it holds NuGet packages (a glob cannot tell it from a monorepo's `packages/`), and plus `out`, a
 * TypeScript project's compiled copy of its sources.
 */
const PICKER_EXCLUDED_DIRS = ['obj', 'bin', 'build', 'dist', '.git', '.vs', '.generated', 'node_modules', '.venv', 'venv',
  '.inferpal', 'out'];

const HISTORY_KEY = 'inferpal.promptHistory';
const HISTORY_MAX = 50;
const STATUS_POLL_MS = 30_000;
/** How long an approval card waits unanswered before a window without the focus says so — the Core's
 *  `ApprovalCard.AttentionDelay`, the number Visual Studio flashes its taskbar at. */
const APPROVAL_ATTENTION_MS = 15_000;
/** How long an answer runs before its end is announced — the Core's `ChatTurnPolicy.LongRunSeconds`. The host decides
 *  for a turn it answered (`announceEnd`); this is for one whose request failed, when the host may be gone. */
const LONG_RUN_SECONDS = 30;

/** Written as a command: a `/`, then a name that starts with a letter or a digit and has no `/` or `\` — the Core's
 *  `SlashCommandRouter.LooksLikeCommand`. A pasted path (`/home/me/main.py crashes`) or a comment (`// why`, a block
 *  comment) is a question, never "unknown command". */
export function looksLikeCommand(text: string): boolean {
  const typed = text.trimStart();
  if (typed.length < 2 || typed[0] !== '/') {
    return false;
  }
  const name = typed.slice(1).split(/[ \t\r\n]/, 1)[0];
  return /^[\p{L}\p{N}]/u.test(name) && !/[/\\]/.test(name);
}

/** A context chip waiting for the next question: its label, its content, and the file it came from. */
/** `origin`: the file the chip's text comes from, whole or part — what the workspace's ignore files are asked about. */
type PendingAttachment = { name: string; content: string; sourcePath?: string; origin?: string; auto?: boolean };
/** A regenerated question's turn, as it was sent: the prompt, whether it was a code action, the relevant-code query. */
type TurnReplay = { prompt: string; codeAction: boolean; query?: string };

export class ChatViewProvider implements vscode.WebviewViewProvider {
  public static readonly viewId = 'inferpal.chat';

  private view: vscode.WebviewView | undefined;
  private readonly transcript: WvTranscriptItem[] = [];
  private models: string[] = [];
  private model = '';
  private busy = false;
  /** Ids for the `/import` cards of this session: an Apply names the card it came from. */
  private importCards = 0;
  /** A code action's changes wait in the Refactor Preview: the model step is over, so Stop has nothing left to cancel
   *  in the host — what it stops is the preview, closed by the preview's own Discard. */
  private codeActionPreviewOpen = false;
  /** Stop was pressed during this turn. Before `chat/send` leaves — the excerpt being sized, a mention resolved, a
   *  template expanded — the host has no turn for `chat/cancel` to stop: read before the turn is sent, or it went anyway. */
  private stopPressed = false;

  /** Channels that have already said why they are degraded, so a per-keystroke path says it once. */
  private readonly saidOnce = new Set<string>();
  /** Step-by-step pause in force: its banner holds the only Resume button, and the transcript does
   *  not carry it, so hydrate posts it again. */
  private stepPaused = false;
  private streamText = '';
  private approvalSeq = 0;
  /** The message is kept with the answer: a rehydration rebuilds the webview from the transcript,
   *  which carries no card, so every card still waiting is posted again (see hydrate). */
  private readonly pendingApprovals = new Map<number, { message: string; card: ApprovalCard | null; resolve: (answer: number) => void }>();
  /** Input cards waiting for the user (an MCP server's form), by the host's card id: posted again by a rehydration, like
   *  the approval cards. */
  private readonly pendingInputs = new Map<string, { card: InputCard; resolve: (answer: InputFormAnswer | null) => void }>();
  /** Read-only plan mode, as the host holds it (the Plan segment of the mode switch). */
  private planMode = false;
  /** Whether the editor listeners feeding the welcome screen are set (once per extension life). */
  private editorWatch = false;
  private editorContextTimer: NodeJS.Timeout | undefined;

  // ── VS-parity state pushed to the webview ───────────────────────────────────
  private status: WvBackendStatus | null = null;
  private commands: WvSlashCommand[] = [];
  private plan: WvPlan | null = null;
  private contextWindow = 0;
  private toolBubblesExpanded = false;
  /** The search for the draft's chips: waiting for typing to pause, then running — cancelled as the person types on. */
  private draftTimer: ReturnType<typeof setTimeout> | undefined;
  private draftSearch: vscode.CancellationTokenSource | undefined;
  /** The files whose found-while-typing chip the person removed: not found again for this draft — cleared once sent. */
  private removedAutoChips: string[] = [];
  /** The build task that failed last — its banner is read from the panel as it stands, at every change and every
   *  redraw; null after a build that passed, once closed, or once its errors were sent to the model. */
  private failedBuild: string | null = null;
  /** Counts the build tasks that ended: a banner computed late never replaces a later build's verdict. */
  private buildSeq = 0;
  /** `runEndSoundEnabled` as last read: what decides for a turn whose request failed — the host decides for the others. */
  private runEndNotice = true;
  /** The conversation drawn compact: Language and appearance › Density, in the host config. */
  private compact = false;
  private promptTokens = 0;
  private lastTokens = 0;
  /** Running token total for the thread and the time of its first turn — the export's statistics
   *  header asks for them, and the Visual Studio window has always kept them (`_sessionTokens`,
   *  `_sessionStartTime`). Here there was nothing: the TypeScript exporter wrote no header at all,
   *  so nobody noticed. Reset to zero with the rest of the thread. */
  private sessionTokens = 0;
  private sessionStart: number | null = null;
  private statusTimer: NodeJS.Timeout | undefined;
  /** Context chips (slash attachChip effects, @-mentions, "+" menu), consumed by the next turn. */
  private pendingAttachments: PendingAttachment[] = [];
  /** What the last question was sent WITH — its words before the "📎 Attached" line, and its chips —, tied to its
   *  bubble: Regenerate resends that, as the Visual Studio window resends `_lastSent`. A reloaded bubble has its words. */
  private lastSent: {
    question: WvTranscriptItem;
    /** The prompt the turn sent (an /explain's instruction and code excerpt), and how: replayed as such. */
    text: string;
    codeAction: boolean;
    query?: string;
    /** The question as its bubble showed it when the turn started (an /explain's line naming the excerpt included). */
    shown: string;
    attachments: PendingAttachment[];
  } | undefined;
  /** The chips a regenerated question carries again — never the ones waiting in the composer for the next question. */
  private resendAttachments: PendingAttachment[] | undefined;
  /** The notes a regenerated question's run read: asked again, it carries them (`chat/send` `notes`). */
  private resendNotes: string[] | undefined;
  /** Ids of the notes typed during a turn (`chat/steer`, `chat/noteApplied`). */
  private noteSeq = 0;
  /** How the last turn ended, and how many have: a queued message is sent after a turn that ended normally only —
   *  after a Stop, an error or a lost host it goes back to the input box (see {@link afterTurn}). */
  private turnOutcome: 'normal' | 'stopped' | 'failed' = 'normal';
  private turnsEnded = 0;
  /** Files pinned into every request, as the host reports them. */
  private pins: string[] = [];
  private mentionCats: WvMentionCategory[] = [];

  /** The first-run card: looking for a server, proposing models, downloading one. */
  private readonly setupFlow: SetupFlow;

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly getHost: () => HostClient | undefined,
    private readonly getActiveEditor: () => vscode.TextEditor | undefined,
    private readonly getDiagnostics: () => Promise<string | null>,
    private readonly log: (line: string) => void,
  ) {
    this.setupFlow = new SetupFlow({
      host: () => this.getHost(),
      show: (view) => this.post({ type: 'setup', view }),
      log: (line) => this.log(line),
      applied: (model, restartNeeded) => this.setupApplied(model, restartNeeded),
      openSettings: async () => {
        await vscode.commands.executeCommand('inferpal.openSettings');
      },
    });
    this.watchBuilds();
  }

  /** The first-run card saved its choice: the chat takes the model, or the host restarts for a server of another type. */
  private async setupApplied(model: string, restartNeeded: boolean): Promise<void> {
    // ⚠ This window's model setting wins over the shared one at the next activation (and at a restart): left as it was,
    // it brought the old model back over the one just chosen. configSaved writes it for the same reason.
    try {
      await vscode.workspace.getConfiguration('inferpal').update('model', model, vscode.ConfigurationTarget.Workspace);
    } catch (err) {
      this.gestureFailed('model setting', err);
    }
    if (restartNeeded) {
      await vscode.commands.executeCommand('inferpal.restartHost');
      return;
    }
    this.model = model;
    this.sharedEcho.defaultModel = model;
    // A model just downloaded joins the picker.
    await this.refreshModelList();
    this.hydrate();
    await this.pollBackendStatus();
  }

  dispose(): void {
    if (this.statusTimer) {
      clearInterval(this.statusTimer);
    }
  }

  resolveWebviewView(view: vscode.WebviewView): void {
    this.log('[chat] resolving webview view');
    this.view = view;
    view.webview.options = {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, 'media')],
    };
    view.webview.html = renderChatHtml(view.webview, this.context.extensionUri);
    this.log('[chat] webview html set');
    this.watchEditorContext();
    // Nobody awaits this promise: a handler that throws past its own guards would be an unhandled
    // rejection, absent from the output channel and invisible to the user.
    view.webview.onDidReceiveMessage((msg: WebviewToExt) =>
      this.onMessage(msg).catch((err) => {
        this.log(`[chat] ${msg.type} failed: ${String(err)}`);
        void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
      }),
    );
    view.onDidDispose(() => {
      if (this.view === view) {
        this.view = undefined;
      }
      // A card the user can no longer answer must fail closed.
      this.denyAllPending();
    });
  }

  /**
   * Re-renders the chat in the language just applied (i18n.setLanguage). The strings are baked into the page when it
   * is built, so a new language needs a new page — whose 'ready' hydrates the conversation back into it.
   */
  relocalize(): void {
    if (this.view) {
      this.view.webview.html = renderChatHtml(this.view.webview, this.context.extensionUri);
    }
  }

  /**
   * Shows an approval card in the chat and resolves with the user's answer
   * (0 deny / 1 once / 2 always / 3 the card's narrow rule). Returns undefined when the card cannot be
   * shown (no resolved view, or no turn running) — the caller then falls back to a modal dialog.
   */
  async requestApproval(message: string, token?: CancellationToken, card: ApprovalCard | null = null): Promise<number | undefined> {
    // ⚠ Outside a turn, no card: a card in a conversation nobody is watching holds the tool (a repository's MCP server
    // asked about when the host starts), and the next Enter in an empty composer answered it — "Allow once" given to a
    // question nobody read, while the card the user was reading still waited. The modal asks instead, as in Visual
    // Studio, whose inline card exists only during a turn.
    if (!this.view || !this.busy) {
      return undefined;
    }
    // Surface the card: reveal the view if it is hidden (agent runs can outlive focus) — WITHOUT taking the keyboard:
    // the user may be typing in an editor, and the keys that answer a card must never be ones typed for something else.
    if (!this.view.visible) {
      try {
        this.view.show(true);
      } catch {
        // reveal unavailable — the card still lands in the retained webview
      }
      if (!this.view) {
        return undefined; // disposed while revealing
      }
    }
    const id = ++this.approvalSeq;
    const answer = new Promise<number>((resolve) => this.pendingApprovals.set(id, { message, card, resolve }));
    // Turn cancelled while the card is up: deny, and retire the card in the webview so
    // it cannot be answered into a run that no longer exists (ghost card).
    const cancelSub = token?.onCancellationRequested(() => this.dismissApproval(id));
    this.post({ type: 'approval', id, message, card });
    const attention = ChatViewProvider.attentionWhileWaiting(
      t('Inferpal is waiting for your approval.'), () => this.pendingApprovals.has(id), answer);
    try {
      return await answer;
    } finally {
      attention.dispose();
      cancelSub?.dispose();
    }
  }

  /**
   * A card left unanswered while its author is elsewhere says so outside the panel: a notification for as long as it
   * waits, gone with the answer — never one left behind after it. VS Code gives an extension no way to flash the taskbar.
   * ⚠ And if the window has the focus then, as soon as it loses it: the card waits as long once its author has gone.
   */
  private static attentionWhileWaiting(title: string, waiting: () => boolean, answer: Promise<unknown>): vscode.Disposable {
    let notified = false;
    let watch: vscode.Disposable | undefined;
    const notify = (): void => {
      if (notified || !waiting()) {
        return;
      }
      notified = true;
      void vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title }, () => answer);
    };
    const timer = setTimeout(() => {
      if (!vscode.window.state.focused) {
        notify();
      } else {
        watch = vscode.window.onDidChangeWindowState((state) => {
          if (!state.focused) {
            notify();
          }
        });
      }
    }, APPROVAL_ATTENTION_MS);
    return { dispose: () => { clearTimeout(timer); watch?.dispose(); } };
  }

  /**
   * Shows the card an MCP server's form is filled on and resolves with what the user did; null when it cannot be shown
   * (no view, no turn). The host checks the values: a card that comes back with the same id and what to fix replaces
   * the one drawn.
   */
  async requestInput(card: InputCard, token?: CancellationToken): Promise<InputFormAnswer | null> {
    // As for an approval: outside a turn, nobody is watching the conversation the card would wait in.
    if (!this.view || !this.busy) {
      return null;
    }
    // Shown WITHOUT taking the keyboard: the keys typed for something else must never fill or send a form.
    if (!this.view.visible) {
      try {
        this.view.show(true);
      } catch {
        // reveal unavailable — the card still lands in the retained webview
      }
      if (!this.view) {
        return null;
      }
    }
    const answer = new Promise<InputFormAnswer | null>((resolve) => this.pendingInputs.set(card.id, { card, resolve }));
    // The turn stopped while the card waited: the card goes, and the host leaves its line.
    const cancelSub = token?.onCancellationRequested(() => this.dismissInput(card.id, null));
    this.post({ type: 'inputForm', card });
    // Under the card's own title, already in the user's language.
    const attention = ChatViewProvider.attentionWhileWaiting(card.title, () => this.pendingInputs.has(card.id), answer);
    try {
      return await answer;
    } finally {
      attention.dispose();
      cancelSub?.dispose();
    }
  }

  /** Takes an input card away, its wait answered with `answer` (null: not shown). */
  private dismissInput(id: string, answer: InputFormAnswer | null): void {
    const pending = this.pendingInputs.get(id);
    if (!pending) {
      return; // already answered
    }
    this.pendingInputs.delete(id);
    this.post({ type: 'inputDismiss', id });
    pending.resolve(answer);
  }

  /** Takes a card away: denied (0) by default; withdrawn (4) when a note took it away, which is no answer of the person's
   *  and is never counted as one. */
  private dismissApproval(id: number, answer: 0 | 4 = 0): void {
    const pending = this.pendingApprovals.get(id);
    if (!pending) {
      return; // already answered
    }
    this.pendingApprovals.delete(id);
    this.post({ type: 'approvalDismiss', id });
    pending.resolve(answer);
  }

  /** New conversation: clears both the host history and the local transcript. */
  async resetConversation(): Promise<void> {
    const host = this.getHost();
    // ⚠ Without the host, a new conversation is only a blank screen: the one shown is not archived, and the host's
    // next start brings it back (a thread with no conversation reloads `last_session`). It stays, and the remedy is said.
    if (!host?.isRunning && this.hasConversation()) {
      this.hostForGesture();
      return;
    }
    if (host?.isRunning) {
      try {
        await host.chatReset();
      } catch (err) {
        // The host refused (a turn is in flight): clearing the webview anyway desyncs the two —
        // an empty transcript over a full host history, and the running turn's answer then lands
        // alone in a "new" conversation carrying the old context.
        this.log(`[chat] reset refused: ${String(err)}`);
        void vscode.window.showWarningMessage(
          t('A turn is still running — stop it before starting a new conversation.'));
        return;
      }
    }
    // Archive after the host accepted the reset — fire-and-forget so the UI clears
    // immediately while the utility model names the session in the background.
    this.archiveConversation();
    this.clearTranscript();
    this.streamText = '';
    this.plan = null;
    this.promptTokens = 0;
    this.lastTokens = 0;
    this.sessionTokens = 0;
    this.sessionStart = null;
    this.hydrate();
  }

  /**
   * Set from the host's spawn until its first restore is done (or the start failed): a question sent meanwhile waits.
   * ⚠ The start brings the last conversation back only when the thread shows none: a question asked during it became
   * "the conversation on screen", was saved over `last_session`, and the conversation it should have restored was gone.
   */
  private hostStarting = false;
  /** `typed`: the composer's text, given back if a refusal meets it once the start is over. */
  private readonly queuedPrompts: { text: string; typed: boolean }[] = [];

  /** Called by the activator just before it spawns the host. */
  onHostStarting(): void {
    this.hostStarting = true;
  }

  /** What the host runs beyond a turn, as last seen (status poll, end of each turn), worded for a crash. */
  private runningIfItCrashes: string | undefined;

  /** Refreshes {@link runningIfItCrashes}; a failed read keeps the last one. */
  private async refreshRunningWork(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      this.runningIfItCrashes = (await host.runningWork()).ifItCrashes ?? undefined;
    } catch {
      // keeps what was last seen
    }
  }

  /**
   * Called by the activator when the host crashed: what it was running stopped with it, and no one is left to ask —
   * said from the last list seen (at most a status poll old).
   */
  onHostCrashed(): void {
    this.sayStoppedWork(this.runningIfItCrashes);
    this.runningIfItCrashes = undefined;
  }

  /** Called by the activator after it stopped a host: the work that host took with it, said in the thread. */
  sayStoppedWork(notice: string | undefined): void {
    if (notice) {
      this.sayOutOfTurn(notice);
    }
  }

  /**
   * A notice that comes outside a turn — a /task's report, the host's own notices, the work a stopped host took with
   * it: kept in the conversation AND drawn.
   * ⚠ Appended only, it was saved and never shown until something redrew the whole thread: "the conversation was not
   * forgotten, it will come back" stayed invisible while it came back.
   */
  private sayOutOfTurn(text: string): void {
    const item: WvTranscriptItem = { role: 'assistant', text, timestamp: ChatViewProvider.now(), notice: true };
    this.append(item);
    this.post({ type: 'assistant', text, timestamp: item.timestamp!, notice: true });
  }

  /** Called by the activator when the host could not start: what waited goes to the "not running" answer. */
  onHostStartFailed(): void {
    this.releaseQueuedPrompts();
  }

  /** The questions that waited for the start are being sent: the end of their turns does not send the queue too. */
  private releasingQueue = false;

  /** The start is over: the questions that waited are sent now, in order. */
  private releaseQueuedPrompts(): void {
    this.hostStarting = false;
    const queued = this.queuedPrompts.splice(0);
    this.postQueue();
    if (queued.length > 0) {
      void (async () => {
        // ⚠ One sender at a time: each of these turns ends in send(), which sends the next queued message — sent from
        // both, the second found a turn running and was refused.
        this.releasingQueue = true;
        try {
          for (const q of queued) {
            await this.send(q.text, q.typed);
          }
        } finally {
          this.releasingQueue = false;
        }
        // What was queued meanwhile (Ctrl+Enter during those turns) goes now.
        if (!this.busy) {
          this.afterTurn();
        }
      })();
    }
  }

  /** Called by the activator once the host handshake finished. */
  async onHostStarted(): Promise<void> {
    try {
      await this.onHostReady();
    } finally {
      this.releaseQueuedPrompts();
    }
  }

  private async onHostReady(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    host.setChatEvents({
      onToken: (text) => {
        this.streamText += text;
        this.post({ type: 'token', text });
      },
      onStep: (text) => this.post({ type: 'status', text }),
      // ⚠ The text was THROWN AWAY here, while the host streamed it with no throttle: a
      // reasoning model's thinking phase showed nothing but a frozen status. On the agent path the
      // tail IS step progress; in plain chat the host sends nothing and the generic indicator takes
      // over.
      onThinking: (text) => this.post({ type: 'thinking', text }),
      onPlan: (plan) => {
        this.plan = { goal: plan.goal, steps: plan.steps.map((text) => ({ text, status: 'pending' })) };
        this.post({ type: 'plan', plan: this.plan });
      },
      onStepUpdate: (update) => {
        if (this.plan && update.index >= 0 && update.index < this.plan.steps.length) {
          this.plan.steps[update.index] = { ...this.plan.steps[update.index], status: update.status };
          this.post({ type: 'plan', plan: this.plan });
        }
      },
      onInputReceipt: (receipt) => {
        // Kept with the conversation, where the card was: what the person sent, and to whom.
        this.append({ role: 'input', text: receipt.saved, timestamp: ChatViewProvider.now() });
        this.post({ type: 'inputReceipt', id: receipt.id, saved: receipt.saved });
      },
      onSetupPullProgress: (progress) => this.setupFlow.onPullProgress(progress),
      onTool: (tool) => {
        const item: WvTranscriptItem = {
          role: 'tool',
          text: tool.name,
          toolInput: tool.input,
          toolOutput: tool.output,
          toolDiff: tool.diff ?? undefined,
          hasErrors: tool.hasErrors,
          timestamp: ChatViewProvider.now(),
        };
        this.append(item);
        this.post({
          type: 'tool',
          name: tool.name,
          input: tool.input,
          output: tool.output,
          hasErrors: tool.hasErrors,
          diff: item.toolDiff,
          timestamp: item.timestamp!,
          expanded: this.toolBubblesExpanded,
        });
      },
      onStreamReset: () => {
        this.streamText = '';
        this.post({ type: 'streamReset' });
      },
      onStepPaused: () => {
        this.stepPaused = true;
        this.post({ type: 'stepPaused' });
      },
      onStepResumed: () => {
        this.stepPaused = false;
        this.post({ type: 'stepResumed' });
      },
      onNoteApplied: (id, afterCalls) => this.noteRead(id, afterCalls),
      // A persistent bubble, like the VS front-end — not an ephemeral status line wiped by the next setBusy.
      onTaskFinished: (text) => this.sayOutOfTurn(text),
      onNotice: (text) => this.sayOutOfTurn(text),
    });

    this.model = vscode.workspace.getConfiguration('inferpal').get<string>('model', '') || host.info?.defaultModel || '';
    // ⚠ This window's model (its workspace setting) wins over the shared default, and the host must answer with it
    // too: bare `/model`, titles, compaction summaries, `/commit`, `/check`, `/task` read the host's default. For this
    // session only — opening a workspace is not a pick, and the other editor keeps its model.
    if (this.model && this.model !== host.info?.defaultModel) {
      try {
        await host.modelsUseForSession(this.model);
      } catch (err) {
        this.log(`[chat] models/useForSession failed: ${String(err)}`);
      }
    }
    try {
      // The chat models only: a question goes to the model picked here, and an embedding model answers none.
      this.models = (await host.modelsListByKind()).chat;
    } catch (err) {
      // ⚠ This is NOT the unreachable-backend path: all three providers swallow the HTTP failure
      // and return an EMPTY list, so `models/list` SUCCEEDS. This catch only ever sees a host or
      // RPC failure. The empty list goes through the success path, and the view names it there
      // (a disabled "no model listed" option).
      this.log(`[chat] models/list failed: ${String(err)}`);
      this.models = this.model ? [this.model] : [];
    }
    // The code's default model, absent from the server, is replaced first and said: the chat answers without a click.
    await this.adoptDefaultModel(host);
    // Then the first-run card, when nothing was chosen yet and the server does not answer, or it has no chat model.
    const setupReason = await host.setupNeeded().catch((err: unknown) => {
      this.log(`[chat] setup/needed failed: ${String(err)}`);
      return null;
    });
    if (setupReason) {
      void this.setupFlow.start(setupReason);
    }

    // Slash autocomplete data + UI-relevant config bits (best-effort, defaults on failure).
    try {
      this.commands = await host.commandList();
    } catch (err) {
      this.log(`[chat] command/list failed: ${String(err)}`);
      this.commands = [];
    }
    try {
      this.mentionCats = await host.mentionCategories();
    } catch (err) {
      this.log(`[chat] mention/categories failed: ${String(err)}`);
      this.mentionCats = [];
    }
    try {
      this.pins = (await host.pinsList()).pins;
    } catch (err) {
      this.log(`[chat] pins/list failed: ${String(err)}`);
      this.pins = [];
    }
    try {
      const cfg = JSON.parse(await host.configGet()) as {
        contextWindowSize?: number; toolBubblesExpanded?: boolean; defaultModel?: string; agentModeEnabled?: boolean;
        chatDensity?: string; runEndSoundEnabled?: boolean;
      };
      // The window the host measures against (the loaded one when smaller), not the setting: read from the
      // configuration, the ring fell back to the configured window after every save.
      this.contextWindow = await host.contextWindow().catch(() => cfg.contextWindowSize ?? 0);
      this.toolBubblesExpanded = cfg.toolBubblesExpanded === true;
      this.runEndNotice = cfg.runEndSoundEnabled !== false;
      this.compact = cfg.chatDensity === 'compact';
      this.sharedEcho = { defaultModel: cfg.defaultModel, agentModeEnabled: cfg.agentModeEnabled };
    } catch (err) {
      this.log(`[chat] config/get failed: ${String(err)}`);
    }

    // ⚠ A restarted host (settings change, crash → Restart, new workspace root) starts in AGENT mode while the switch
    // still shows Plan: the next question ran with the write tools under a screen that says read-only, and clicking
    // Plan again did nothing (the switch already said so). The mode goes back to the host — or, refused, the switch
    // shows what the host really does, and says so.
    if (this.planMode) {
      try {
        this.planMode = await host.planMode(true);
      } catch (err) {
        this.log(`[chat] plan mode not restored after a host restart: ${String(err)}`);
        this.planMode = false;
      }
      if (!this.planMode) {
        void vscode.window.showWarningMessage(
          t('The assistant restarted and could not return to plan mode: the agent can edit files again.'));
      }
    }

    this.startStatusPolling();
    await this.pollBackendStatus();

    // ⚠ A host that restarted (settings change, crash → Restart) starts with an EMPTY history while
    // the thread still shows the conversation: the next question went out with no context, and the
    // model "forgot" what is on screen. Save what is shown, then load it back so the host rebuilds
    // its history from it.
    // ⚠ A CONVERSATION on screen, not any line: at a first start with the backend down, the status poll above has just
    // put its notice in the thread — read as "the host restarted", that one notice was saved over the last
    // conversation (shared with Visual Studio) and the restore below never ran.
    const conversationShown = this.hasConversation();
    if (conversationShown) {
      // ⚠ TWO doors into that amnesia, and only one of them throws. When the rebuild does not
      // happen the failure IS the bug this block exists to prevent, so it is said in the thread —
      // next to the messages the model can no longer see — and not in the output channel.
      let rebuildFailure: string | null = null;
      try {
        await host.sessionSave('last_session', this.snapshot());
        const back = await host.sessionLoad('last_session');
        if (back && back.messages.length > 0) {
          this.applySession(back.messages, back.nextTurnTokens ?? 0);
          return; // applySession hydrates
        }
        // Nothing threw and the slot came back empty: the host declined to hand it over (see
        // HostServer, which returns null when the auto-save belongs to another workspace).
        rebuildFailure = t('the restarted assistant did not take the saved conversation back');
      } catch (err) {
        rebuildFailure = ChatViewProvider.errorText(err);
      }
      this.log(`[chat] history rebuild after host restart failed: ${rebuildFailure}`);
      this.append({
        role: 'assistant',
        text: t(
          '⚠ The assistant restarted and the conversation above could NOT be handed back to it: {0}. It will answer your next question without that context — restate what matters, or start a new conversation.',
          rebuildFailure,
        ),
        timestamp: ChatViewProvider.now(),
        notice: true,
      });
    }

    // Continuity across restarts: bring back the auto-saved conversation, like the VS VM.
    if (!conversationShown) {
      // What was said while starting (backend unreachable, default model replaced) stays, under the conversation.
      const startNotices = this.transcript.filter((m) => m.notice);
      try {
        const last = await host.sessionLoad('last_session');
        if (last && last.messages.length > 0) {
          this.applySession(last.messages, last.nextTurnTokens ?? 0);
          if (startNotices.length > 0) {
            this.transcript.push(...startNotices);
            this.hydrate();
          }
          return; // applySession hydrates
        }
      } catch (err) {
        // Said where the conversation is: the first turn's auto-save writes over the slot (the host kept a copy of a
        // damaged file, and its message names it).
        this.log(`[chat] last-session restore failed: ${String(err)}`);
        this.append({
          role: 'assistant',
          text: t('⚠ The last conversation could not be restored: {0}', ChatViewProvider.errorText(err)),
          timestamp: ChatViewProvider.now(),
          notice: true,
        });
      }
    }
    this.hydrate();
  }

  /** Called after the settings panel saved: re-reads the UI-relevant config bits. */
  async configSaved(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      const cfg = JSON.parse(await host.configGet()) as {
        contextWindowSize?: number; toolBubblesExpanded?: boolean; defaultModel?: string; agentModeEnabled?: boolean;
        chatDensity?: string; runEndSoundEnabled?: boolean;
      };
      // The window the host measures against (the loaded one when smaller), not the setting: read from the
      // configuration, the ring fell back to the configured window after every save.
      this.contextWindow = await host.contextWindow().catch(() => cfg.contextWindowSize ?? 0);
      this.toolBubblesExpanded = cfg.toolBubblesExpanded === true;
      this.runEndNotice = cfg.runEndSoundEnabled !== false;
      // The open conversation follows a new density at once.
      this.compact = cfg.chatDensity === 'compact';
      this.post({ type: 'density', compact: this.compact });
      // The chat sends its own model and agent mode on every turn (workspace settings): what the settings panel
      // CHANGED is adopted here, or it changed nothing. Only a change is adopted, so a model picked for this
      // workspace survives a save that did not touch it.
      const settings = vscode.workspace.getConfiguration('inferpal');
      if (cfg.defaultModel && cfg.defaultModel !== this.sharedEcho.defaultModel) {
        this.model = cfg.defaultModel;
        try {
          await settings.update('model', cfg.defaultModel, vscode.ConfigurationTarget.Workspace);
        } catch (err) {
          // ⚠ Not just the log: this per-workspace copy WINS over the host's value at the next
          // activation, so losing it silently reverts the model the panel just saved. The
          // `stateChange` path two hundred lines below already says so for the same failure.
          this.gestureFailed('model setting', err);
        }
      }
      if (typeof cfg.agentModeEnabled === 'boolean' && cfg.agentModeEnabled !== this.sharedEcho.agentModeEnabled) {
        try {
          await settings.update('agentMode', cfg.agentModeEnabled, vscode.ConfigurationTarget.Workspace);
        } catch (err) {
          this.gestureFailed('agent mode setting', err);
        }
      }
      this.sharedEcho = { defaultModel: cfg.defaultModel, agentModeEnabled: cfg.agentModeEnabled };
    } catch (err) {
      // ⚠ The panel saved and this re-read NOTHING: context window, tool bubbles, model and agent
      // mode all stay as they were, right after the user pressed Save. Silence here is the state
      // where the settings screen and the chat disagree and nobody is told which one is live.
      this.gestureFailed('settings reload', err);
    }
    try {
      this.commands = await host.commandList();
    } catch {
      // keep the previous autocomplete list
    }
    try {
      const listed = (await host.modelsListByKind()).chat;
      // ⚠ An empty list must not REPLACE the previous one: it arrives through the success path,
      // not the catch (see bootstrap), so saving the settings with the backend down would silently
      // empty the picker. The previous list is kept only when the new one is empty — a backend that
      // REALLY lost models must still be able to remove them.
      if (listed.length > 0) {
        this.models = listed;
      }
    } catch {
      // host or RPC failure: keep the previous list (see bootstrap for why this is not the
      // "backend unreachable" case).
    }
    try {
      // ⚠ The chips follow the setting, as in Visual Studio: a file pinned, unpinned or disabled (#) in the settings
      // panel changes every prompt at once, and chips read only at start-up kept showing the old list.
      this.pins = (await host.pinsList()).pins;
    } catch (err) {
      this.log(`[chat] pins/list after a settings save failed: ${String(err)}`);
    }
    this.hydrate();
    void this.pollBackendStatus();
  }

  // ── Backend status badge (connection + VRAM) ───────────────────────────────

  private startStatusPolling(): void {
    if (this.statusTimer) {
      clearInterval(this.statusTimer);
    }
    this.statusTimer = setInterval(() => void this.pollBackendStatus(), STATUS_POLL_MS);
  }

  /**
   * The badge with no host running: the HOST's sentence, never "backend unreachable" — the backend was not asked. One
   * writer for the poll and the redraw: a redraw that drew it without the sentence flipped the badge to "unreachable"
   * at every hydrate (a refusal, a notice) until the next poll, half a minute later.
   */
  private static noHostStatus(): WvBackendStatus {
    return { connected: false, vramBadge: '', hostDown: hostUnavailableMessage() };
  }

  private async pollBackendStatus(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      this.status = null;
      this.post({ type: 'backendStatus', status: ChatViewProvider.noHostStatus() });
      return;
    }
    // Known down, not unknown: at start the status is null and the bootstrap has just listed the models itself.
    const wasDown = this.status !== null && !this.status.connected;
    let edgeNotice: string | null = null;
    try {
      const s = await host.backendStatus();
      this.status = { connected: s.connected, vramBadge: s.vramBadge, refused: s.refused ?? null, server: s.server ?? null };
      edgeNotice = s.edgeNotice ?? null;
    } catch {
      this.status = { connected: false, vramBadge: '' };
    }
    this.post({ type: 'backendStatus', status: this.status });
    await this.refreshRunningWork();
    // ⚠ An outage ANNOUNCES itself, it is not to be guessed. Without this line, losing the backend
    // mid-session only changed the colour of a dot here - the Visual Studio window has always put
    // the sentence in the thread. The Core says WHEN (edge crossed, first successful check silent);
    // the adapter only renders it.
    if (edgeNotice) {
      this.append({ role: 'assistant', text: edgeNotice, timestamp: ChatViewProvider.now(), notice: true });
      this.hydrate();
    }
    // The backend came back: the list read while it was down is empty, and a default nobody chose may now be
    // replaceable. A server started after the editor — the ordinary order — left the picker on "no model listed"
    // and every question on the default until a reload.
    if (wasDown && this.status.connected) {
      await this.refreshModelsAfterReconnect(host);
    }
  }

  /**
   * The model picker's list, re-read from the server when the menu opens — the menu is already drawn from the list the
   * view holds. A host or a server that cannot answer leaves it as it is, an empty answer too (see configSaved): the
   * menu's own lines say when nothing is listed or the backend is down.
   */
  private async refreshModelList(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      const listed = (await host.modelsListByKind()).chat;
      if (listed.length > 0 && listed.join('\n') !== this.models.join('\n')) {
        this.models = listed;
        this.post({ type: 'models', models: listed });
      }
    } catch (err) {
      this.log(`[chat] models/list on opening the model picker failed: ${String(err)}`);
    }
  }

  /**
   * The slash commands, re-read as one starts being typed — the popup already works from the list the view holds. A
   * host that cannot answer leaves it as it is, an empty answer too.
   */
  private async refreshCommandList(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      const listed = await host.commandList();
      if (listed.length > 0) {
        this.commands = listed;
        this.post({ type: 'commands', commands: listed });
      }
    } catch (err) {
      this.log(`[chat] command/list on typing a slash command failed: ${String(err)}`);
    }
  }

  private async refreshModelsAfterReconnect(host: HostClient): Promise<void> {
    try {
      const listed = (await host.modelsListByKind()).chat;
      if (listed.length > 0) {
        this.models = listed;
      }
    } catch (err) {
      this.log(`[chat] models/list after reconnection failed: ${String(err)}`);
    }
    await this.adoptDefaultModel(host);
    this.hydrate();
  }

  /** A default nobody chose, which the backend does not have: the host puts the best installed model in its place and
   *  says so — VS Code has no first run, and every question went to a model that is not there. */
  private async adoptDefaultModel(host: HostClient): Promise<void> {
    if (vscode.workspace.getConfiguration('inferpal').get<string>('model', '')
        || !this.model || this.models.length === 0 || this.models.includes(this.model)) {
      return;
    }
    try {
      const adopted = await host.modelsAdoptDefault();
      if (adopted.model) {
        this.model = adopted.model;
        this.sharedEcho.defaultModel = adopted.model;
        if (adopted.notice) {
          this.append({ role: 'assistant', text: adopted.notice, timestamp: ChatViewProvider.now(), notice: true });
        }
      }
    } catch (err) {
      this.log(`[chat] models/adoptDefault failed: ${String(err)}`);
    }
  }

  // ── Sessions ────────────────────────────────────────────────────────────────

  /** Persistable view of the transcript (same shape the VS extension saves): the whole conversation, hidden part included. */
  private snapshot(): SavedMessage[] {
    return toSavedMessages(this.wholeTranscript());
  }

  /**
   * The conversation, entries hidden from the view first — never the marker that says they are hidden.
   * ⚠ Both editors write the same session files, and only this view is bounded: saved from the view alone, a Visual
   * Studio conversation past the cap loses its oldest turns at the first auto-save, the next archive or `/branch`.
   */
  private wholeTranscript(): WvTranscriptItem[] {
    return [...this.hiddenEntries, ...(this.droppedEntries > 0 ? this.transcript.slice(1) : this.transcript)];
  }

  /** Empties the conversation: the view, its hidden part and the marker's count, together. */
  private clearTranscript(): void {
    this.transcript.length = 0;
    this.hiddenEntries = [];
    this.droppedEntries = 0;
  }

  /** Replaces the transcript with a restored session (host history already rebuilt). */
  /** `nextTurnTokens`: the loaded conversation as the host measured it — what the next question will send. */
  private applySession(messages: SavedMessage[], nextTurnTokens = 0): void {
    this.clearTranscript();
    this.transcript.push(...toTranscript(messages));
    this.trimTranscript();
    this.streamText = '';
    this.plan = null;
    // Another conversation: its counters start over, as for a new one (and as the VS window restores).
    // Kept, the gauge showed the previous fill and the export counted the previous tokens and duration.
    // ⚠ The fill is the LOADED conversation's, never zero: zero hides the gauge, and a long conversation restored
    // at every start showed none until the next question (the VS window measures it on restore).
    this.promptTokens = nextTurnTokens;
    this.lastTokens = 0;
    this.sessionTokens = 0;
    this.sessionStart = null;
    this.hydrate();
  }

  /** Set when the auto-save last failed and the user was told; cleared by the next one that works. */
  private autoSaveFailureTold = false;

  /** Is a conversation on screen — a question or an answer, not only notices? A thread of notices alone (backend
   *  unreachable, a slash command's output) is saved over nothing: the last conversation is worth more. */
  private hasConversation(): boolean {
    return this.transcript.some((m) => !m.notice);
  }

  private autoSaveLast(): void {
    const host = this.getHost();
    if (!host?.isRunning || !this.hasConversation()) {
      return;
    }
    host.sessionSave('last_session', this.snapshot()).then(
      () => {
        this.autoSaveFailureTold = false;
      },
      (err: unknown) => {
        this.log(`[chat] auto-save failed: ${String(err)}`);
        // ⚠ Said ONCE: this runs on every turn and its causes last (a full disk, a read-only
        // settings folder), so a notice per turn buries the conversation it is about. Re-armed by
        // the next save that works, otherwise "once" becomes "once in the life of the view".
        if (this.autoSaveFailureTold) {
          return;
        }
        this.autoSaveFailureTold = true;
        this.append({
          role: 'assistant',
          text: t(
            '⚠ This conversation could NOT be saved for next time: {0}. It is still here, but it will not come back on its own — export it if you want to keep it.',
            ChatViewProvider.errorText(err),
          ),
          timestamp: ChatViewProvider.now(),
          notice: true,
        });
        this.hydrate();
      },
    );
  }

  /**
   * `/branch <n>`: forks the conversation host-side and continues in the branch. The transcript
   * sent over is the display one (tool names and timestamps only exist here), minus the trailing
   * `/branch …` bubble — the command itself is not part of the conversation being forked.
   * Always returns a note to show: the host sends no bubble of its own for this command, so a fork
   * that failed without one closed the turn empty.
   */
  private async branchAtTurn(turn: number): Promise<readonly [note: string, branched: boolean]> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return [hostUnavailableMessage(), false];
    }
    const messages = this.snapshot();
    const last = messages[messages.length - 1];
    if (last?.role === 'user' && looksLikeCommand(last.content)) {
      messages.pop();
    }
    try {
      const branch = Number.isFinite(turn) ? await host.sessionBranch(turn, messages) : null;
      if (!branch) {
        return [t('No turn {0} in this conversation — no branch was created.', String(turn)), false];
      }
      this.applySession(branch.messages, branch.nextTurnTokens ?? 0);
      return [branch.message, true];
    } catch (err) {
      this.gestureFailed('branch', err);
      return [ChatViewProvider.errorText(err), false];
    }
  }

  /**
   * `/branch [args]`: decided host-side on THIS transcript — the list the fork runs on — never on the
   * host's history, where a question carries the RAG auto-context and compaction renumbers turns. The
   * decision is then carried out here: a fork, a switch, or a message.
   */
  private async decideBranch(args: string): Promise<readonly [note: string, rehydrate: boolean]> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return [hostUnavailableMessage(), false];
    }
    const messages = this.snapshot();
    const last = messages[messages.length - 1];
    if (last?.role === 'user' && looksLikeCommand(last.content)) {
      messages.pop();
    }
    try {
      const decision = await host.sessionBranchCommand(args, messages);
      if (typeof decision.forkTurn === 'number') {
        return await this.branchAtTurn(decision.forkTurn);
      }
      if (decision.switchTo) {
        const [switched, failure] = await this.switchToSession(decision.switchTo);
        return [switched ? decision.message ?? '' : failure, false];
      }
      return [decision.message ?? '', false];
    } catch (err) {
      this.gestureFailed('branch command', err);
      return [ChatViewProvider.errorText(err), false];
    }
  }

  /**
   * `/branch <name>`: switching to a branch is a plain session load. The host has already answered
   * "Switched to branch X", so a failure returns its cause for the caller to show instead of that claim.
   */
  private async switchToSession(name: string): Promise<readonly [switched: boolean, failure: string]> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return [false, hostUnavailableMessage()];
    }
    try {
      const loaded = name ? await host.sessionLoad(name) : null;
      if (!loaded) {
        return [false, t('Branch {0} could not be loaded — it may have been deleted.', name)];
      }
      this.applySession(loaded.messages, loaded.nextTurnTokens ?? 0);
      return [true, ''];
    } catch (err) {
      this.gestureFailed('branch switch', err);
      return [false, ChatViewProvider.errorText(err)];
    }
  }

  /**
   * Archives the conversation being discarded, as the screen holds it (`session/archive`), the way the VS front-end
   * does on `/clear`. The snapshot is captured synchronously — the caller clears the transcript right after. ⚠ No model
   * call before the save: the host archived the slot at the reset, this writes over that archive at once, and the
   * host renames it after its title in the background.
   */
  private archiveConversation(): void {
    const host = this.getHost();
    if (!host?.isRunning || this.transcript.length === 0) {
      return;
    }
    const messages = this.snapshot();
    void (async () => {
      try {
        await host.sessionArchive(messages);
      } catch (err) {
        this.log(`[chat] archive failed: ${String(err)}`);
        void vscode.window.showWarningMessage(
          t(
            'The conversation you just left could not be archived: {0}. It was not saved under a name, and the chat has already been cleared.',
            ChatViewProvider.errorText(err),
          ),
        );
      }
    })();
  }

  /** Command: save the conversation under a user-chosen name (LLM-suggested by default). */
  async saveSessionCommand(): Promise<void> {
    const host = this.getHost();
    // ⚠ A palette command that does NOTHING is indistinguishable from one that failed: with no
    // host, we say so. An empty conversation, on the other hand, is a normal case — there is
    // nothing to save.
    if (!host?.isRunning) {
      void vscode.window.showWarningMessage(hostUnavailableMessage());
      promptOpenFolder();
      return;
    }
    if (this.transcript.length === 0) {
      return;
    }
    const first = this.transcript.find((m) => m.role === 'user' && !m.notice && !m.note)?.text ?? '';
    const suggested = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Window, title: t('Naming the session…') },
      async () => {
        try {
          return (await host.sessionTitle(first)).title;
        } catch {
          // Backend down or model missing: fall back to the plain snippet, never block the save.
          return first.slice(0, 35);
        }
      },
    );
    const name = await vscode.window.showInputBox({
      prompt: t('Session name'),
      value: suggested.replace(/[\\/:*?"<>|\n]+/g, ' ').trim(),
      // The store writes these as '_' (no file name may hold them): accepted, two names would share one file, and the
      // "replace?" question below — asked on the typed name — would never come before the other session is overwritten.
      validateInput: (v) =>
        /[\\/:*?"<>|\x00-\x1f]/.test(v) ? t('A session name cannot contain {0}', '\\ / : * ? " < > |') : undefined,
    });
    if (!name) {
      return;
    }
    // 'last_session' is the auto-save slot (overwritten every turn): a user save under
    // that name would silently vanish. Same pattern as /plan's reserved set: suffix it.
    const safeName =
      name.trim().toLowerCase() === 'last_session' ? `${name.trim()}-session` : name.trim();
    // A palette command that fails in silence cannot be told apart from a broken one: a store that refuses says why.
    try {
      // Saving under a name another session already uses replaces that session: ask first, like a delete.
      const existing = await host.sessionList();
      // ⚠ An unreadable session is absent from `sessions`, and its file is still there: left out of
      // this check, the save replaced it without the question — unreadable ⇒ absent ⇒ overwritten.
      const taken = [...existing.sessions.map((s) => s.name), ...existing.unreadable];
      if (taken.some((n) => n.toLowerCase() === safeName.toLowerCase())) {
        const replaceLabel = t('Replace');
        const answer = await vscode.window.showWarningMessage(
          t('A session named {0} already exists. Replace it?', safeName),
          { modal: true },
          replaceLabel,
        );
        if (answer !== replaceLabel) {
          return;
        }
      }
      await host.sessionSave(safeName, this.snapshot());
      void vscode.window.showInformationMessage(t('Session saved: {0}', safeName));
    } catch (err) {
      void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
    }
  }

  /** Command: pick a saved session and restore it (host history + transcript). */
  async loadSessionCommand(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      void vscode.window.showWarningMessage(hostUnavailableMessage());
      promptOpenFolder();
      return;
    }
    // A palette command that does nothing cannot be told apart from a broken one: every failure says so — the
    // session list included, which is a call to the host too.
    try {
      const pick = await this.pickSession(t('Pick a session to load'));
      if (!pick) {
        return;
      }
      const loaded = await host.sessionLoad(pick);
      if (!loaded) {
        void vscode.window.showWarningMessage(
          t('Session {0} could not be loaded — it may have been deleted.', pick),
        );
        return;
      }
      this.applySession(loaded.messages, loaded.nextTurnTokens ?? 0);
    } catch (err) {
      void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
    }
  }

  /** Command: pick a saved session and delete it. */
  async deleteSessionCommand(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      void vscode.window.showWarningMessage(hostUnavailableMessage());
      promptOpenFolder();
      return;
    }
    // The session list is a call to the host too: its failure says why, like the delete's.
    try {
      const pick = await this.pickSession(t('Pick a session to delete'));
      if (!pick) {
        return;
      }
      // Irreversible, on the same sessions folder the Visual Studio window lists — which confirms first too.
      const deleteLabel = t('Delete');
      const answer = await vscode.window.showWarningMessage(
        t('Delete session {0}? This cannot be undone.', pick),
        { modal: true },
        deleteLabel,
      );
      if (answer !== deleteLabel) {
        return;
      }
      if (await host.sessionDelete(pick)) {
        void vscode.window.showInformationMessage(t('Session deleted: {0}', pick));
      } else {
        void vscode.window.showWarningMessage(
          t('Session {0} was not found — it may already have been deleted.', pick),
        );
      }
    } catch (err) {
      void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
    }
  }

  private pickSession(placeholder: string): Promise<string | undefined> {
    return pickSession(this.getHost()!, placeholder);
  }

  /**
   * Command: export the conversation to a Markdown or text file.
   *
   * ⚠ The document is rendered by the **Core** exporter, through the host — the same one the
   * Visual Studio window uses. Rendered here instead, it loses the stats header (model, turns, tool
   * calls, tokens, date, duration) and ignores the `.txt` filter this very dialog offers: choosing
   * *Text* writes Markdown into a `.txt`.
   */
  async exportCommand(): Promise<void> {
    // A thread of notices alone (a slash command's output, "backend unreachable") is not a conversation.
    if (!this.hasConversation()) {
      void vscode.window.showInformationMessage(t('Nothing to export — the conversation is empty.'));
      return;
    }
    const host = this.getHost();
    if (!host?.isRunning) {
      void vscode.window.showErrorMessage(hostUnavailableMessage());
      return;
    }
    const target = await vscode.window.showSaveDialog({
      filters: { Markdown: ['md'], Text: ['txt'] },
      defaultUri: vscode.Uri.file('inferpal-chat.md'),
    });
    if (!target) {
      return;
    }
    try {
      const document = await host.chatExport({
        asPlainText: target.fsPath.toLowerCase().endsWith('.txt'),
        messages: this.wholeTranscript().map((item) => ({
          role: item.role,
          // For a tool turn, `text` IS the tool name and `toolOutput` the body.
          name: item.role === 'tool' ? item.text : undefined,
          content: (item.role === 'tool' ? item.toolOutput : item.text) ?? '',
          timestamp: item.timestamp,
          // A slash command shown as typed is a notice, a note typed during a run is part of its turn: exported, never
          // counted as a turn of their own.
          notice: item.notice === true || item.note !== undefined,
        })),
        sessionTokens: this.sessionTokens,
        durationSeconds: this.sessionStart === null
          ? undefined : Math.round((Date.now() - this.sessionStart) / 1000),
      });
      await vscode.workspace.fs.writeFile(target, Buffer.from(document, 'utf8'));
      void vscode.window.showInformationMessage(t('Conversation exported: {0}', target.fsPath));
    } catch (err) {
      void vscode.window.showErrorMessage(t('Export failed: {0}', ChatViewProvider.errorText(err)));
    }
  }

  // ── Prompt history (persisted workspace-side, navigated in the webview) ─────

  private historyEntries(): string[] {
    return this.context.workspaceState.get<string[]>(HISTORY_KEY, []);
  }

  /** Mirror of the VS AppendPromptHistory policy: dedupe the top entry, cap to max. */
  private appendHistory(prompt: string): string[] {
    const entries = this.historyEntries().slice();
    if (entries[entries.length - 1] !== prompt) {
      entries.push(prompt);
      while (entries.length > HISTORY_MAX) {
        entries.shift();
      }
      void this.context.workspaceState.update(HISTORY_KEY, entries);
    }
    return entries;
  }

  // ── Webview → extension ─────────────────────────────────────────────────────

  private async onMessage(msg: WebviewToExt | { type: 'clientError'; message: string }): Promise<void> {
    switch (msg.type) {
      case 'clientError':
        this.log(`[webview] ${msg.message}`);
        // ⚠ A log line is not a message — the rule this file states for gestures, and this is the
        // one channel that exists ONLY to report failure. An unhandled error in the chat view
        // leaves a UI that has quietly stopped updating: from the outside, a button that does
        // nothing, which is indistinguishable from a button that did something.
        // ⚠ Said ONCE and re-armed on 'ready' (a webview that loaded again): a render that throws
        // on every message would otherwise turn the notice into the noise nobody reads.
        this.sayOnce('webviewError',
          t('The Inferpal chat view hit an error and may not be up to date. Reload the window if it stops responding.'));
        return;
      case 'ready':
        this.log('[chat] webview ready');
        // The webview is fresh, so the notice above is worth saying again if it breaks anew.
        this.saidOnce.delete('webviewError');
        this.hydrate();
        // A reloaded webview draws the first-run card again where it was.
        if (this.setupFlow.current) {
          this.post({ type: 'setup', view: this.setupFlow.current });
        }
        // And the banner of a build that failed, built before the view was.
        if (this.failedBuild !== null) {
          this.postBuildBanner();
        }
        return;
      case 'setupAction':
        await this.setupFlow.onAction(msg);
        return;
      case 'send':
        await this.send(msg.text, msg.typed === true);
        return;
      case 'steer':
        await this.steer(msg.text);
        return;
      case 'queue':
        await this.enqueue(msg.text);
        return;
      case 'unqueue':
        if (msg.index >= 0 && msg.index < this.queuedPrompts.length) {
          this.queuedPrompts.splice(msg.index, 1);
        }
        this.postQueue();
        return;
      case 'importApply': {
        // The card's Apply runs the command the user could have typed: the numbers left ticked, or none. The card is
        // marked used only when the command can run — send() says why it cannot (a turn running, no host), and the
        // card comes back live.
        const card = this.transcript.find((i) => i.importCard?.id === msg.id)?.importCard;
        if (!card || card.applied) {
          return;
        }
        const ready = !this.busy && (this.hostStarting || this.getHost()?.isRunning === true);
        card.applied = ready;
        const file = /\s/.test(card.file) ? `"${card.file}"` : card.file;
        // With what the card showed: against a plan that moved since, its numbers would name other items.
        await this.send(`/import ${card.source} ${file} apply ${msg.numbers.length > 0 ? msg.numbers.join(',') : 'none'} #${card.fingerprint}`);
        if (!ready) {
          this.hydrate();
        }
        return;
      }
      case 'fixProblems':
      case 'fixBuild': {
        // ⚠ The errors travel as the chip the @problems picker makes: typed in the prompt, "@problems" names no file,
        // nothing expands it, and the request reached the model as text without a single error. No problems left (fixed
        // since the banner was drawn): the panel says so and nothing is sent. During a turn, refused before the chip is
        // made — it would ride the next question —, and the build banner stays to try again.
        if (this.busy) {
          void vscode.window.showWarningMessage(t('A turn is still running — wait for it to finish or stop it, then try again.'));
          return;
        }
        const before = this.pendingAttachments.length;
        await this.resolveMention('problems');
        if (this.pendingAttachments.length > before) {
          if (msg.type === 'fixBuild') {
            this.failedBuild = null;
            this.post({ type: 'buildBanner', banner: null });
          }
          await this.send(msg.text);
        }
        return;
      }
      case 'showProblems':
        try {
          await vscode.commands.executeCommand('workbench.actions.view.problems');
        } catch (err) {
          this.gestureFailed('show problems', err);
        }
        return;
      case 'dismissBuildBanner':
        this.failedBuild = null;
        return;
      case 'cancel':
        if (this.codeActionPreviewOpen) {
          try {
            // Discarding resolves the pending applyEdit with false: the turn ends on "Rewrite discarded".
            await vscode.commands.executeCommand('refactorPreview.discard');
          } catch (err) {
            this.gestureFailed('cancel', err);
          }
          return;
        }
        if (this.busy) {
          this.stopPressed = true;
        }
        try {
          await this.getHost()?.chatCancel();
        } catch (err) {
          // Stop that does not reach the host leaves the turn running, and the button has already
          // gone back to its idle look.
          this.gestureFailed('cancel', err);
        }
        return;
      case 'reset':
        await this.resetConversation();
        return;
      case 'pickModel': {
        this.model = msg.model;
        try {
          await vscode.workspace.getConfiguration('inferpal').update('model', msg.model, vscode.ConfigurationTarget.Workspace);
        } catch (err) {
          // A settings.json with a syntax error, a read-only file: the pick still applies to this window and
          // the host below, but it will not survive a reload — say so instead of stopping half-way.
          this.log(`[chat] model setting not saved: ${String(err)}`);
          void vscode.window.showWarningMessage(t('Inferpal could not save this setting: {0}', ChatViewProvider.errorText(err)));
        }
        // Also push the pick into the host's shared config: without it, `/model` (no argument)
        // kept answering the OLD model and every Model Router role that falls back to
        // DefaultModel (session titles, code actions) silently stayed on it.
        // Read-modify-write of the full JSON — config/update replaces the whole object.
        await this.pushModelToHost(msg.model);
        return;
      }
      case 'approvalAnswer': {
        const pending = this.pendingApprovals.get(msg.id);
        this.pendingApprovals.delete(msg.id);
        pending?.resolve(msg.answer);
        return;
      }
      case 'inputAnswer': {
        const pending = this.pendingInputs.get(msg.id);
        this.pendingInputs.delete(msg.id);
        pending?.resolve({ action: msg.action, values: msg.values });
        return;
      }
      case 'mentionQuery': {
        const items = await this.mentionSuggestions(msg.query);
        this.post({ type: 'mentionSuggestions', items, query: msg.query });
        return;
      }
      case 'openApprovalDiff': {
        try {
          const doc = await vscode.workspace.openTextDocument({ language: 'diff', content: msg.text });
          await vscode.window.showTextDocument(doc, { preview: true });
        } catch (err) {
          // The user wants to read what they are about to allow: a diff that cannot open is said, not silent.
          this.log(`[chat] approval diff failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      }
      case 'xrayToggle': {
        // Applies the toggle host-side (next turns) and re-renders the refreshed panel.
        const host = this.hostForGesture();
        if (!host) {
          return;
        }
        try {
          const panel = await host.xrayToggle(msg.id, msg.enabled);
          this.post({ type: 'xrayPanel', panel });
        } catch (err) {
          // The checkbox already flipped in the webview, and the host refuses while a turn runs: put its panel
          // back so the section is shown as the host will send it, and say why.
          this.log(`[chat] xray/toggle failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
          try {
            this.post({ type: 'xrayPanel', panel: await host.xrayPanel() });
          } catch (panelErr) {
            this.log(`[chat] xray/panel failed: ${String(panelErr)}`);
          }
        }
        return;
      }
      case 'runReview': {
        const host = this.hostForGesture();
        if (!host) {
          return;
        }
        try {
          this.post({ type: 'runReview', runId: msg.runId, review: await host.runReview(msg.runId) });
        } catch (err) {
          this.gestureFailed('run review', err);
        }
        return;
      }
      case 'runFile': {
        // The card disabled its actions on the click: every way out answers it, or it would stay disabled.
        const host = this.hostForGesture();
        if (!host) {
          this.post({ type: 'runReview', runId: msg.runId, message: hostUnavailableMessage() });
          return;
        }
        try {
          const result = await host.runFile(msg.runId, msg.path, msg.action);
          this.post({ type: 'runReview', runId: msg.runId, review: result.review, message: result.message });
        } catch (err) {
          // Refused while a turn runs, or failed: the card is drawn again as it stands, under the reason.
          this.log(`[chat] run file failed: ${String(err)}`);
          const reason = ChatViewProvider.errorText(err);
          try {
            this.post({ type: 'runReview', runId: msg.runId, review: await host.runReview(msg.runId), message: reason });
          } catch {
            this.post({ type: 'runReview', runId: msg.runId, message: reason });
          }
        }
        return;
      }
      case 'copyText':
        try {
          await vscode.env.clipboard.writeText(msg.text);
        } catch (err) {
          // A clipboard the session refuses: nothing was copied, and the copy button gives no feedback of its own.
          this.log(`[chat] copy failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      case 'regenerate':
        await this.regenerate();
        return;
      case 'continue':
        await this.continueTurn();
        return;
      case 'retryConnection':
        // ⚠ A retry is a gesture: with no host to ask, it names the host's remedy (open a folder, restart the host).
        this.hostForGesture();
        await this.pollBackendStatus();
        return;
      case 'listModels':
        await this.refreshModelList();
        return;
      case 'listCommands':
        await this.refreshCommandList();
        return;
      case 'openSessions':
        await this.loadSessionCommand();
        return;
      case 'menu':
        if (msg.action === 'export') {
          await this.exportCommand();
        } else {
          await vscode.commands.executeCommand('inferpal.openSettings');
        }
        return;
      case 'setMode':
        await this.setMode(msg.mode);
        return;
      case 'openFile':
        try {
          await vscode.window.showTextDocument(vscode.Uri.file(msg.path), { preview: false });
        } catch (err) {
          // A file of the result bar that no longer opens (deleted since): said, not a dead link.
          this.log(`[chat] open ${msg.path} failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      case 'openXray':
        await this.openXray();
        return;
      case 'mentionSearch': {
        // ⚠ Said ONCE, and re-armed on the first search that works again: this runs on every
        // keystroke, so a toast per call would be the noise that stops being read. An empty
        // suggestion list otherwise reads as "no file matches", which is a fact about the
        // workspace, not about a search that never ran.
        const host = this.getHost();
        if (!host?.isRunning) {
          this.sayOnce('mentionSearch', hostUnavailableMessage());
          return;
        }
        try {
          const items = await host.mentionSearch(msg.category, msg.query);
          this.post({ type: 'mentionResults', category: msg.category, query: msg.query, items });
          this.saidOnce.delete('mentionSearch');
        } catch (err) {
          this.log(`[chat] mention/search failed: ${String(err)}`);
          this.sayOnce('mentionSearch', ChatViewProvider.errorText(err));
        }
        return;
      }
      case 'resolveMention':
        await this.resolveMention(msg.category, msg.value);
        return;
      case 'resumeStep':
        try {
          await this.getHost()?.chatResumeStep();
        } catch (err) {
          // Resume that does not reach the host leaves the turn paused for good: the one gesture
          // that can unblock it is the one that just did nothing.
          this.gestureFailed('resumeStep', err);
        }
        return;
      case 'draft':
        this.draftChanged(msg.text);
        return;
      case 'removeChip':
        if (msg.index >= 0 && msg.index < this.pendingAttachments.length) {
          const [removed] = this.pendingAttachments.splice(msg.index, 1);
          // A chip found while typing that the person removed is not found again for this draft.
          if (removed.auto && removed.origin) {
            this.removedAutoChips.push(removed.origin);
          }
          this.postChips();
        }
        return;
      case 'pinActive': {
        // Pins the active file (or one picked from disk) into every request, as the Visual Studio window does.
        const host = this.hostForGesture();
        if (!host) {
          return;
        }
        try {
          const active = this.getActiveEditor();
          let path = active?.document.uri.scheme === 'file' ? active.document.uri.fsPath : undefined;
          if (!path) {
            const picked = await vscode.window.showOpenDialog({ canSelectMany: false });
            path = picked?.[0]?.fsPath;
          }
          if (!path) {
            return;
          }
          this.applyPins(await host.pinsAdd(path));
        } catch (err) {
          this.log(`[chat] pin failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      }
      case 'unpin': {
        const host = this.hostForGesture();
        if (!host) {
          return;
        }
        try {
          this.applyPins(await host.pinsRemove(msg.path));
        } catch (err) {
          this.log(`[chat] unpin failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      }
      case 'attachActive': {
        const editor = this.getActiveEditor();
        if (!editor || editor.document.uri.scheme !== 'file') {
          void vscode.window.showInformationMessage(t('No file is open in the editor.'));
          return;
        }
        this.addChip('📄 ' + vscode.workspace.asRelativePath(editor.document.uri), editor.document.getText(),
                     editor.document.uri.fsPath, editor.document.uri.fsPath);
        return;
      }
      case 'attachSelection': {
        const editor = this.getActiveEditor();
        const text = editor && !editor.selection.isEmpty ? editor.document.getText(editor.selection) : '';
        if (!editor || text.length === 0) {
          void vscode.window.showInformationMessage(t('The selection is empty.'));
          return;
        }
        this.addChip('✂ ' + vscode.workspace.asRelativePath(editor.document.uri), text, undefined, editor.document.uri.fsPath);
        return;
      }
      case 'attachBrowse': {
        const picked = await vscode.window.showOpenDialog({ canSelectMany: false });
        if (!picked || picked.length === 0) {
          return;
        }
        try {
          const doc = await vscode.workspace.openTextDocument(picked[0]);
          this.addChip('📄 ' + (picked[0].path.split('/').pop() ?? picked[0].fsPath), doc.getText(), picked[0].fsPath,
                       picked[0].fsPath);
        } catch (err) {
          // A binary or unreadable file: the user picked it, and no chip appearing reads as a broken menu.
          this.log(`[chat] attachBrowse failed: ${String(err)}`);
          void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        }
        return;
      }
    }
  }

  // ── Typed mentions: chip resolution ─────────────────────────────────────────

  /** Shows the host's pinned files, and what it had to say (the cap reached, a failed save). */
  private applyPins(result: { pins: string[]; notice?: string | null }): void {
    this.pins = result.pins;
    this.post({ type: 'pins', pins: this.pins });
    if (result.notice) {
      void vscode.window.showInformationMessage(result.notice);
    }
  }

  /** `sourcePath`: the file a whole-file chip holds, as Visual Studio's `SourcePath` — never for a selection or a
   * synthetic chip (clipboard, problems), which are not that file. `origin`: the file the text comes from, a selection's
   * included — judged against the workspace's ignore files when the question is sent. */
  private addChip(name: string, content: string, sourcePath?: string, origin?: string): void {
    // A chip that holds part of its file says how much, on the chip and under the question.
    const capped = capAttachment(content, 60_000);
    this.pendingAttachments.push({
      name: capped.cut ? ChatViewProvider.cutLabel(name, capped) : name,
      // ⚠ Part of a file is not the file: named as attached, its code past the cut was ALSO kept out of the relevant
      // code the host adds (it skips an attached file's chunks, "already in the prompt").
      sourcePath: capped.cut ? undefined : sourcePath,
      content: capped.text,
      origin,
    });
    this.postChips();
  }

  /** Which of `paths` the workspace's ignore files exclude, one entry per path (an empty path is never excluded). */
  private static async excluded(host: HostClient, paths: string[]): Promise<({ notice: string; forModel: string } | undefined)[]> {
    if (!paths.some((p) => p.length > 0)) {
      return paths.map(() => undefined);
    }
    const answer = await host.exclusionsCheck(paths);
    return paths.map((p, i) => (p.length > 0 && answer.notices[i]
      ? { notice: answer.notices[i] ?? '', forModel: answer.forModel[i] ?? '' }
      : undefined));
  }

  /** "big.cs (first 60,000 of 183,402 characters)", in the interface language. */
  private static cutLabel(name: string, capped: CappedText): string {
    const n = (value: number): string => value.toLocaleString(vscode.env.language);
    return t('{0} (first {1} of {2} characters)', name, n(capped.shown), n(capped.total));
  }

  private postChips(): void {
    this.post({ type: 'chips', chips: this.chipsShown() });
  }

  /** The chips as the composer draws them — a redraw too, or it showed the found-while-typing ones as plain ones. */
  private chipsShown(): { name: string; auto: boolean }[] {
    return this.pendingAttachments.map((a) => ({ name: a.name, auto: a.auto === true }));
  }

  /**
   * The draft changed: its chips — the code the search finds for it, as Visual Studio's 🔮 chips — go at once, and the
   * search runs again once typing pauses (800 ms, Visual Studio's debounce). Not during a turn: typed then, the text is a
   * note to the running turn.
   */
  private draftChanged(text: string): void {
    this.cancelDraftSearch();
    if (this.pendingAttachments.some((a) => a.auto)) {
      this.pendingAttachments = this.pendingAttachments.filter((a) => !a.auto);
      this.postChips();
    }
    const host = this.getHost();
    if (!host?.isRunning || this.busy) {
      return;
    }
    this.draftTimer = setTimeout(() => void this.searchDraft(host, text), 800);
  }

  private cancelDraftSearch(): void {
    if (this.draftTimer) {
      clearTimeout(this.draftTimer);
      this.draftTimer = undefined;
    }
    this.draftSearch?.cancel();
    this.draftSearch?.dispose();
    this.draftSearch = undefined;
  }

  /** The draft's chips, from the host; a file already attached by hand is left as it is. */
  private async searchDraft(host: HostClient, text: string): Promise<void> {
    this.draftTimer = undefined;
    const search = new vscode.CancellationTokenSource();
    this.draftSearch = search;
    try {
      const chips = await host.typingChips(text, search.token);
      if (search.token.isCancellationRequested || this.draftSearch !== search) {
        return;
      }
      for (const chip of chips) {
        // Already attached by hand, or removed for this draft. "Same file?" as the file system answers it: the editor
        // writes a lowercase drive letter, the index an uppercase one.
        const held = (p: string | undefined): boolean => p !== undefined && samePath(p, chip.path);
        if (this.pendingAttachments.some((a) => held(a.sourcePath) || held(a.origin)) || this.removedAutoChips.some(held)) {
          continue;
        }
        // Capped as every chip is (addChip). Its file counts as attached unless cut: it holds the file's best chunks for
        // this very search, and the turn's relevant code would otherwise send them again, ahead of another file's.
        const capped = capAttachment(chip.content, 60_000);
        // Several folders open: the name carries its folder, or two index.ts read as one.
        const file = (vscode.workspace.workspaceFolders?.length ?? 0) > 1 ? vscode.workspace.asRelativePath(chip.path) : chip.name;
        const name = chip.lines ? `${file} ${chip.lines}` : file;
        this.pendingAttachments.push({
          name: capped.cut ? ChatViewProvider.cutLabel(name, capped) : name,
          content: capped.text,
          sourcePath: capped.cut ? undefined : chip.path,
          origin: chip.path,
          auto: true,
        });
      }
      if (chips.length > 0) {
        this.postChips();
      }
    } catch (err) {
      // Best-effort, as in Visual Studio: no chip, and the turn's own auto-context still runs.
      if (!search.token.isCancellationRequested) {
        this.log(`[chat] rag/typingChips failed: ${String(err)}`);
      }
    } finally {
      if (this.draftSearch === search) {
        this.draftSearch = undefined;
      }
      search.dispose();
    }
  }

  /** Materializes an instant/selected mention: clipboard and problems are editor-side (they read
   * panels only the adapter can see); everything else goes through the host (mention/resolve).
   *
   * ⚠ `debugger` is served by the HOST, not here: answered editor-side it attaches the session's
   * name and type, where Visual Studio attaches the stop reason, the call stack and the locals —
   * and the docs promise the break state for both editors. Through the host it reads the same port
   * as `debug_inspect`, so the two editors answer the same question the same way. */
  private async resolveMention(category: string, value?: string): Promise<void> {
    // Each source can refuse (a clipboard a remote session denies, a host call that fails): the chip
    // that never appears reads as a broken mention, so the failure is said, not only logged.
    try {
      switch (category) {
        case 'clipboard': {
          const text = await vscode.env.clipboard.readText();
          if (text.trim().length > 0) {
            this.addChip('📋 clipboard', text);
          } else {
            void vscode.window.showInformationMessage(t('The clipboard is empty.'));
          }
          return;
        }
        case 'problems': {
          const report = await this.getDiagnostics();
          if (report && report.trim().length > 0) {
            this.addChip('⚠ problems', report);
          } else {
            void vscode.window.showInformationMessage(t('No problems in the Problems panel.'));
          }
          return;
        }
        default: {
          const host = this.getHost();
          if (!host?.isRunning) {
            // ⚠ The rule three lines above, applied to the most common refusal of all: with no
            // folder open the host never starts, so every host-backed mention did nothing at all
            // — no chip, no message — while the palette commands next door all say it.
            void vscode.window.showWarningMessage(hostUnavailableMessage());
            promptOpenFolder();
            return;
          }
          const result = await host.mentionResolve(category, value);
          if (result.name && result.content) {
            this.addChip(result.name, result.content);
          } else if (result.notice) {
            // Nothing to attach, and a reason for it: a message, never silence.
            void vscode.window.showInformationMessage(result.notice);
          }
          return;
        }
      }
    } catch (err) {
      this.log(`[chat] mention ${category} failed: ${String(err)}`);
      void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
    }
  }

  /** Shows the chat and its Context X-Ray panel — the settings' "Open Context X-Ray" link. */
  async revealXray(): Promise<void> {
    await vscode.commands.executeCommand('inferpal.chat.focus');
    await this.openXray();
  }

  private async openXray(): Promise<void> {
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    try {
      const panel = await host.xrayPanel();
      this.post({ type: 'xrayPanel', panel });
    } catch (err) {
      this.gestureFailed('xray/panel', err);
    }
  }

  // ── @-mentions ──────────────────────────────────────────────────────────────

  /** Suggestions for the webview popup: the open tabs first, then a workspace glob. */
  private async mentionSuggestions(query: string): Promise<string[]> {
    const MAX = 12;
    const q = query.toLowerCase();
    const items = new Set<string>();

    // The tabs, never workspace.textDocuments: it also holds what was loaded without a tab — a file an @-mention read,
    // a document an extension opened.
    for (const path of openTabPaths()) {
      // asRelativePath's default: the folder's name first when the workspace has several — the form
      // resolveMention reads back. Without it another folder's file of the same name was attached.
      const rel = vscode.workspace.asRelativePath(vscode.Uri.file(path)).replace(/\\/g, '/');
      if (!q || rel.toLowerCase().includes(q)) {
        items.add(rel);
      }
    }
    if (items.size < MAX && q.length >= 2) {
      try {
        const found = await vscode.workspace.findFiles(
          `**/*${query}*`,
          `**/{${PICKER_EXCLUDED_DIRS.join(',')}}/**`,
          MAX,
        );
        for (const uri of found) {
          items.add(vscode.workspace.asRelativePath(uri).replace(/\\/g, '/'));
        }
      } catch {
        // findFiles unavailable (no workspace) — open editors already listed
      }
    }
    return [...items].slice(0, MAX);
  }

  /** Expands `@relative/path` tokens into fenced attachments appended to the prompt.
   * Reads through `openTextDocument`, so dirty buffers win over disk. Non-file tokens
   * (someone's @handle) simply resolve to nothing and stay as typed. */
  /** `labels`: what the person reads under the question when a file was not sent whole, or not sent at all. */
  private async expandMentions(prompt: string, host: HostClient): Promise<{ text: string; paths: string[]; labels: string[] }> {
    const folders = (vscode.workspace.workspaceFolders ?? []).map((f) => ({ name: f.name, target: f.uri }));
    if (folders.length === 0 || !prompt.includes('@')) {
      return { text: prompt, paths: [], labels: [] };
    }
    const MAX_FILES = 5;
    const MAX_CHARS = 40_000;
    // ⚠ The cap counts files ATTACHED, not tokens seen: five `@property` in pasted code used up the
    // budget before a real `@src/app.py` was even tried — and the file was silently left out.
    // The attempts themselves stay bounded.
    const MAX_ATTEMPTS = 25;
    const tokens = mentionTokens(prompt).slice(0, MAX_ATTEMPTS);

    let attachments = '';
    const paths: string[] = [];
    const labels: string[] = [];
    // ⚠ A file past the cap is NAMED, to both readers: dropped in silence, the model never learnt the person had asked
    // about it, and the person never learnt it was not sent.
    const notAttached: string[] = [];
    for (const token of tokens) {
      const target = resolveMention(token, folders);
      if (!target) {
        break;
      }
      const uri = vscode.Uri.joinPath(target.folder.target, target.relative);
      if (paths.length >= MAX_FILES) {
        try {
          if ((await vscode.workspace.fs.stat(uri)).type & vscode.FileType.File) {
            notAttached.push(token);
          }
        } catch {
          // not a workspace file — plain text, as below
        }
        continue;
      }
      // A token that is no file (someone's @handle, an annotation in pasted code) stays text, and costs the host nothing.
      let isFile = false;
      try {
        isFile = ((await vscode.workspace.fs.stat(uri)).type & vscode.FileType.File) !== 0;
      } catch {
        // not a workspace file
      }
      if (!isFile) {
        continue;
      }
      // ⚠ Excluded by the workspace's ignore files: never read, and named to both readers — the person under the
      // question, the model in the prompt.
      const excluded = await ChatViewProvider.excluded(host, [uri.fsPath]);
      if (excluded[0]) {
        attachments += `\n\n## Not attached: ${token}\n${excluded[0].forModel}`;
        labels.push(excluded[0].notice);
        continue;
      }
      try {
        const doc = await vscode.workspace.openTextDocument(uri);
        const capped = capAttachment(doc.getText(), MAX_CHARS);
        attachments += `\n\n## Attached file: ${token}\n\`\`\`\n${capped.text}\n\`\`\``;
        if (capped.cut) {
          labels.push(ChatViewProvider.cutLabel(token, capped));
        } else {
          // The full path: a folder-qualified token resolved under the host's root would name another file. Only a
          // WHOLE file: the host skips an attached file's chunks, and the part past a cut is in the prompt nowhere.
          paths.push(doc.uri.fsPath);
        }
      } catch {
        // not a workspace file — leave the token as plain text
      }
    }
    if (notAttached.length > 0) {
      attachments += `\n\n## Not attached: ${notAttached.join(', ')}\n`
        + `(at most ${MAX_FILES} files are attached to a question; read_file reads the others)`;
      labels.push(t('{0} (not attached: at most {1} files per question)', notAttached.join(', '), MAX_FILES));
    }
    return { text: prompt + attachments, paths, labels };
  }

  private denyAllPending(): void {
    for (const pending of this.pendingApprovals.values()) {
      pending.resolve(0);
    }
    this.pendingApprovals.clear();
    // The panel the card was on is gone: the person saw it and left it unanswered — a cancel, never "not shown".
    for (const pending of this.pendingInputs.values()) {
      pending.resolve({ action: 'cancel', values: {} });
    }
    this.pendingInputs.clear();
  }

  /** The host stopped or crashed: the approval cards it opened can no longer be answered. */
  onHostStopped(): void {
    this.dismissAllPending();
  }

  /** A model picked while a turn ran, still to be pushed into the host's config. */
  private pendingModelPush: string | undefined;
  /** An agent mode switched while a turn ran, still to be pushed into the host's config. */
  private pendingAgentModePush: boolean | undefined;
  /** The shared config's chat model and agent mode as this view last knew them: what a settings save changed. */
  private sharedEcho: { defaultModel?: string; agentModeEnabled?: boolean } = {};

  /**
   * Pushes the picked model into the host's shared config: without it `/model` kept answering the
   * OLD model, and every Model Router role that falls back to DefaultModel (session titles, code
   * actions) silently stayed on it. Read-modify-write of the full JSON — config/update
   * replaces the whole object.
   * ⚠ config/update holds the turn slot: a pick made DURING a turn was refused and only logged, and
   * the host kept the old model with nothing saying so. Such a pick is replayed when the turn ends.
   */
  private async pushModelToHost(model: string): Promise<void> {
    // ⚠ Silence here is the picker showing one model while Inferpal answers with another — the
    // cost this method's own remark states for the busy branch, which the replay covers.
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    if (this.busy) {
      this.pendingModelPush = model;
      return;
    }
    this.pendingModelPush = undefined;
    try {
      const cfg = JSON.parse(await host.configGet()) as { defaultModel?: string };
      if (cfg.defaultModel !== model) {
        cfg.defaultModel = model;
        await host.configUpdate(JSON.stringify(cfg));
      }
      this.sharedEcho.defaultModel = model;
    } catch (err) {
      this.gestureFailed('model pick → host config', err);
    }
  }

  /**
   * Pushes the chat's agent-mode switch into Inferpal's config, which the settings panel shows. Same slot
   * rule as pushModelToHost: a switch made during a turn is replayed when it ends.
   */
  private async pushAgentModeToHost(enabled: boolean): Promise<void> {
    // Same rule as the model pick: the switch is on screen, and the mode is read from the settings
    // on every turn.
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    if (this.busy) {
      this.pendingAgentModePush = enabled;
      return;
    }
    this.pendingAgentModePush = undefined;
    try {
      const cfg = JSON.parse(await host.configGet()) as { agentModeEnabled?: boolean };
      if (cfg.agentModeEnabled !== enabled) {
        cfg.agentModeEnabled = enabled;
        await host.configUpdate(JSON.stringify(cfg));
      }
      this.sharedEcho.agentModeEnabled = enabled;
    } catch (err) {
      this.gestureFailed('agent mode → host config', err);
    }
  }

  /** Other config/update pushes deferred while a turn runs, by key — a later change replaces an earlier one. */
  private readonly idleWork = new Map<string, () => Promise<void>>();

  /**
   * Runs a push that calls config/update now, or when the running turn ends: the host refuses config/update while
   * a turn holds its slot, and a refused push is lost.
   */
  /**
   * The window's model setting changed outside the picker (the Settings editor, settings.json): the chat follows it,
   * as the host's next start would — for this session (`models/useForSession`), never written to the shared default.
   * Without it, the button and every question kept the old model until the host restarted. The picker's own write
   * lands here too, already applied.
   */
  async followModelSetting(): Promise<void> {
    const configured = vscode.workspace.getConfiguration('inferpal').get<string>('model', '');
    if (!configured || configured === this.model) {
      return;
    }
    this.model = configured;
    this.post({ type: 'models', models: this.models, model: configured });
    // Refused by the host while a turn runs: pushed when it ends. Without a host, its start reads the setting.
    this.runWhenIdle('modelSetting', async () => {
      const host = this.getHost();
      if (host?.isRunning) {
        try {
          await host.modelsUseForSession(configured);
        } catch (err) {
          this.gestureFailed('model setting → host', err);
        }
      }
    });
  }

  runWhenIdle(key: string, work: () => Promise<void>): void {
    if (!this.busy) {
      void work();
      return;
    }
    this.idleWork.set(key, work);
  }

  /** Called wherever a turn ends: pushes a model picked, an agent mode switched or any other deferred push made
   * while it ran — one after the other, since each is a read-modify-write of the whole config. */
  private flushPendingModelPush(): void {
    if (this.busy
        || (this.pendingModelPush === undefined && this.pendingAgentModePush === undefined && this.idleWork.size === 0)) {
      return;
    }
    const deferred = [...this.idleWork.values()];
    this.idleWork.clear();
    void (async () => {
      if (this.pendingModelPush !== undefined) {
        await this.pushModelToHost(this.pendingModelPush);
      }
      if (this.pendingAgentModePush !== undefined) {
        await this.pushAgentModeToHost(this.pendingAgentModePush);
      }
      for (const work of deferred) {
        await work();
      }
    })();
  }

  /** Denies every card still waiting AND retires it in the webview, so none stays clickable. */
  private dismissAllPending(): void {
    for (const id of [...this.pendingApprovals.keys()]) {
      this.dismissApproval(id);
    }
    for (const id of [...this.pendingInputs.keys()]) {
      this.dismissInput(id, null);
    }
  }


  /**
   * The end of a long run, said by a notification while its chat is out of sight — the window in the background or the
   * view hidden — as Visual Studio plays its sound (`runEndSoundEnabled`, the same setting). A failure says so; a turn the
   * person stopped is not announced — they are here. Show the answer brings the chat back, the view's command
   * recreating a view that was closed meanwhile.
   */
  private announceRunEnd(due: boolean, outcome: 'normal' | 'stopped' | 'failed', what: string): void {
    if (!due || outcome === 'stopped') {
      return;
    }
    if (vscode.window.state.focused && this.view?.visible) {
      return;
    }
    const show = t('Show the answer');
    const shown = outcome === 'failed'
      ? vscode.window.showWarningMessage(t('Inferpal stopped on an error.'), show)
      : vscode.window.showInformationMessage(what ? t('Inferpal has finished — {0}.', what) : t('Inferpal has finished.'), show);
    void shown.then((picked) => {
      if (picked === show) {
        void vscode.commands.executeCommand('inferpal.chat.focus');
      }
    });
  }

  /** A turn ended: how, for {@link afterTurn}. Called where each way out of a turn tells the webview it ended. */
  private turnEnded(outcome: 'normal' | 'stopped' | 'failed'): void {
    this.turnOutcome = outcome;
    this.turnsEnded++;
  }

  /**
   * After a turn: the next queued message is sent when it ended normally. After a Stop, an error or a lost host, nothing
   * is sent on its own — the user stopped for a reason: the queue goes back to the input box, and that is said.
   */
  private afterTurn(): void {
    if (this.queuedPrompts.length === 0 || this.hostStarting || this.releasingQueue) {
      return;
    }
    if (this.turnOutcome === 'normal') {
      const next = this.queuedPrompts.shift()!;
      this.postQueue();
      // ⚠ On the next turn of the event loop, never from inside the send that just ended: its caller may still hold
      // state for that turn — Regenerate's chips and notes are cleared only once its send returns, and the queued message
      // left with them.
      setTimeout(() => {
        // A message sent meanwhile took the turn: this one waits for its end, first in line.
        if (this.busy) {
          this.queuedPrompts.unshift(next);
          this.postQueue();
          return;
        }
        void this.send(next.text, next.typed);
      }, 0);
      return;
    }
    const texts = this.queuedPrompts.splice(0).map((q) => q.text);
    this.postQueue();
    this.post({ type: 'restoreDraft', text: texts.join('\n\n') });
    this.sayOutOfTurn(t('The turn did not end normally: the messages waiting to be sent are back in the input box.'));
  }

  private postQueue(): void {
    this.post({ type: 'queue', items: this.queuedPrompts.map((q) => q.text) });
  }

  /**
   * Ctrl+Enter, a note no step can read, a context-menu action: the next message, sent once the turn ends — at once
   * when none runs. `typed`: the composer's text, given back if its turn is refused.
   */
  private async enqueue(text: string, typed = true): Promise<void> {
    const prompt = text.trim();
    if (!prompt) {
      return;
    }
    if (!this.busy && !this.hostStarting) {
      await this.send(prompt, typed);
      return;
    }
    this.queuedPrompts.push({ text: prompt, typed });
    this.postQueue();
  }

  /**
   * Enter during a turn: a note for the run's next step (`chat/steer`), drawn in the turn as waiting, then as read. A
   * turn with no step to read it before — a chat without tools, a code action, a slash command, a turn not yet at the
   * host — refuses it: it goes to the queue instead, never lost.
   */
  private async steer(text: string): Promise<void> {
    const prompt = text.trim();
    if (!prompt) {
      return;
    }
    if (!this.busy) {
      await this.send(prompt, true);   // the turn ended meanwhile: an ordinary message
      return;
    }
    // A slash command is a command to run, never words for the model: it waits for the turn.
    if (looksLikeCommand(prompt)) {
      await this.enqueue(prompt);
      return;
    }
    const host = this.getHost();
    const id = `note-${++this.noteSeq}`;
    // Drawn before the host answers, so the note precedes the step that reads it.
    const item: WvTranscriptItem = { role: 'user', text: prompt, timestamp: ChatViewProvider.now(), note: 'waiting', noteId: id };
    this.append(item);
    this.post({ type: 'note', id, text: prompt, state: 'waiting' });
    let accepted = false;
    if (host?.isRunning) {
      try {
        accepted = (await host.chatSteer(id, prompt)).accepted;
      } catch (err) {
        this.log(`[chat] chat/steer failed: ${String(err)}`);
      }
    }
    if (accepted) {
      // Read by the run: a card waiting is refused — a text never approves — and the note says why; a paused run
      // resumes, to read it at the step it resumes to. Queued instead, neither is touched.
      if (this.pendingApprovals.size === 1) {
        this.dismissApproval([...this.pendingApprovals.keys()][0], 4);
      }
      // A form waiting is set aside the same way: the note is the person's answer to the run, never to the server.
      if (this.pendingInputs.size === 1) {
        this.dismissInput([...this.pendingInputs.keys()][0], { action: 'setAside', values: {} });
      }
      if (this.stepPaused) {
        try {
          await host!.chatResumeStep();
        } catch (err) {
          this.gestureFailed('resumeStep', err);
        }
      }
      return;
    }
    // ⚠ Unless the turn's end already settled it: refused because the run was closing, it was moved to the queue (or
    // back to the input box) with the other notes — queued here too, it was sent twice.
    if (this.transcript.includes(item)) {
      this.dropNote(item);
      await this.enqueue(prompt);
    }
  }

  /** The model read a note: drawn as read, and saved as one. */
  private noteRead(id: string, afterCalls: number): void {
    const item = this.transcript.find((m) => m.noteId === id);
    if (!item || item.note === 'read') {
      return;
    }
    item.note = 'read';
    item.noteAfter = afterCalls;
    this.post({ type: 'note', id, text: item.text, state: 'read', afterCalls });
  }

  /** A note the turn will not read leaves it. */
  private dropNote(item: WvTranscriptItem): void {
    const at = this.transcript.indexOf(item);
    if (at >= 0) {
      this.transcript.splice(at, 1);
    }
    if (item.noteId) {
      this.post({ type: 'noteRemoved', id: item.noteId });
    }
  }

  /**
   * The end of a turn's notes: those the host says were read are drawn as read (a notification can arrive after the
   * result); those still waiting leave the turn — the next message after a turn that ended normally, back in the input
   * box otherwise, said.
   */
  private settleNotes(applied: readonly TurnNote[] | null | undefined, normal: boolean): void {
    for (const n of applied ?? []) {
      this.noteRead(n.id, n.afterCalls ?? 0);
    }
    const waiting = this.transcript.filter((m) => m.note === 'waiting');
    if (waiting.length === 0) {
      return;
    }
    for (const item of waiting) {
      this.dropNote(item);
    }
    const texts = waiting.map((m) => m.text);
    if (normal) {
      this.queuedPrompts.unshift(...texts.map((text) => ({ text, typed: true })));
      this.postQueue();
      return;
    }
    this.post({ type: 'restoreDraft', text: texts.join('\n\n') });
    this.sayOutOfTurn(t('Your note was not read before the turn ended: it is back in the input box.'));
  }

  /** The last question of the thread — never a note typed during its run. */
  private lastQuestion(): WvTranscriptItem | undefined {
    return [...this.transcript].reverse().find((m) => m.role === 'user' && !m.note);
  }

  /**
   * Every message goes through here: once its turn has ended, the next queued message is sent — or, after a turn that
   * did not end normally, the queue goes back to the input box ({@link afterTurn}).
   * @param typed The composer's text: the webview cleared the box as it sent, so a refusal before the turn starts gives
   * it back. Any other gesture (a card, Undo, the editor's menu, Regenerate) leaves the box alone: it may hold a draft.
   */
  private async send(text: string, typed = false, replay?: TurnReplay): Promise<void> {
    const before = this.turnsEnded;
    // The draft's chips leave with the draft. A gesture's message (Explain from the menu, Fix with AI, a queued prompt)
    // is not the draft, which stays in the box: they are not sent with it.
    if (typed) {
      this.cancelDraftSearch();
      this.removedAutoChips = [];
    } else if (this.pendingAttachments.some((a) => a.auto)) {
      this.pendingAttachments = this.pendingAttachments.filter((a) => !a.auto);
      this.postChips();
    }
    try {
      const prompt = text.trim();
      const host = this.getHost();
      if (!prompt) {
        return;
      }
      // ⚠ The composer cannot send during a turn, but other gestures come here too — the editor's context menu
      // (Explain, Fix, Refactor, Doc), the result bar's Undo: dropped in silence, the click did nothing at all.
      if (this.busy) {
        void vscode.window.showWarningMessage(t('A turn is still running — wait for it to finish or stop it, then try again.'));
        return;
      }
      if (this.hostStarting) {
        // ⚠ Said in the thread, and kept with its mark: as a status line — drawn during a turn only — the question left
        // the box with no sign it waited; replayed without `typed`, a refusal after the start (no host, backend down)
        // did not give it back, and the text was lost. Said once per start: the notice is not part of the conversation.
        if (this.queuedPrompts.length === 0) {
          this.sayOutOfTurn(t('Inferpal is starting — your message is sent as soon as it is ready.'));
        }
        this.queuedPrompts.push({ text: prompt, typed });
        this.postQueue();
        return;
      }
      if (!host?.isRunning) {
        // Two states, two remedies: with no folder open the host was never spawned, and
        // "Restart Host" cannot change that (hostStatus.ts). A notice: read as a conversation, the host's next start
        // saved this line over the last one instead of bringing it back.
        // ⚠ Typed, the text is given back: the box was cleared as it sent, and the message was lost with the refusal.
        const said = typed ? `${hostUnavailableMessage()} ${t('Your message was not sent and is back in the input box.')}` : hostUnavailableMessage();
        this.append({ role: 'error', text: said, timestamp: ChatViewProvider.now(), notice: true });
        this.hydrate();
        this.giveBackTyped(text, typed);
        promptOpenFolder();
        return;
      }

      // /xray opens the interactive panel (ephemeral overlay — no transcript entry, no model call).
      if (prompt.split(/\s+/, 1)[0].toLowerCase() === '/xray') {
        await this.openXray();
        return;
      }

      // Slash commands are served by the host and still run when the backend is down — except the ones that ask the
      // model: /explain and /review, known by their name (a template is known only once the host has expanded it).
      const head = prompt.split(/\s+/, 1)[0].toLowerCase();
      const asksTheModel = !looksLikeCommand(prompt) || head === '/explain' || head === '/review';
      if (asksTheModel && (await this.refusedWhileBackendDown(text, typed))) {
        return;
      }

      this.busy = true;
      this.stopPressed = false;
      // Another turn leaves: the last one's Continue is no longer the gesture it was, here and at the next redraw.
      for (const item of this.transcript) {
        delete item.continues;
      }
      this.streamText = '';
      this.plan = null;
      // Like the Visual Studio window (`_sessionStartTime ??= DateTime.Now`): the thread starts at
      // the first turn, not when the panel is opened.
      this.sessionStart ??= Date.now();
      const timestamp = ChatViewProvider.now();
      // Shown as typed. A slash command served without the model marks it a notice below: saved as a question, a reload
      // handed "/models" to the model as something it had been asked (Visual Studio shows no question for one).
      const question: WvTranscriptItem = { role: 'user', text: prompt, timestamp };
      this.append(question);
      // A regenerated question is not typed again: the prompt history keeps what the user wrote.
      const history = replay ? this.historyEntries() : this.appendHistory(prompt);
      this.post({ type: 'turnStarted', prompt, timestamp, history });

      // ⚠ A regenerated question is replayed with what it carried — the same prompt, as a code action if it was one —
      // never dispatched again from its words: resent as words, an /explain answer came back as an agent turn (tools,
      // the chat model) with the whole excerpt pasted into the question and the prompt history.
      if (replay) {
        await this.chatTurn(replay.prompt, host, replay.codeAction, replay.query);
        return;
      }

      // In-place code actions (/fix /refactor /doc): rewrite the active editor's file (or
      // selection) with a native per-hunk preview — never sent to the chat history.
      const codeAction = ChatViewProvider.codeActionKind(prompt);
      if (codeAction) {
        question.notice = true;
        try {
          await this.runCodeAction(codeAction, host, ChatViewProvider.requestOf(prompt));
        } catch (err) {
          // runCodeAction ends the turn itself on every path it returns from; a throw (workspace edit
          // rejected, document closed under it) would otherwise leave the webview busy with no error.
          this.log(`[chat] code action failed: ${String(err)}`);
          this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
        } finally {
          this.busy = false;
          this.autoSaveLast();
        }
        return;
      }

      // /explain and /review stay read-only and editor-side (VS parity): the active
      // document (or selection) is attached and the answer streams into the chat.
      const first = prompt.split(/\s+/, 1)[0].toLowerCase();
      // /test: the selection's tests (or the file's), written by an edit the editor can undo — never on disk behind it.
      if (first === '/test') {
        question.notice = true;
        try {
          await this.runTestGeneration(host, ChatViewProvider.requestOf(prompt));
        } catch (err) {
          this.log(`[chat] /test failed: ${String(err)}`);
          this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
        } finally {
          this.busy = false;
          this.autoSaveLast();
        }
        return;
      }
      if (first === '/explain' || first === '/review') {
        // ⚠ Not in the host's history until the turn reaches it: refused here (no file open, the excerpt failed), it was
        // left an unmarked question, and Regenerate then took back the exchange BEFORE it — the host forgot a question
        // and answer the screen still showed. A notice until the turn starts (runExplainReview clears the mark).
        question.notice = true;
        try {
          await this.runExplainReview(first === '/explain' ? 'explain' : 'review', host, question,
            ChatViewProvider.requestOf(prompt));
        } catch (err) {
          // The editor is read after the turn started: a throw there would leave the provider busy for good,
          // every later message dropped by the busy guard. The chat turn itself never throws.
          this.log(`[chat] ${first} failed: ${String(err)}`);
          this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
        }
        return;
      }

      // Slash commands the host serves headlessly: rendered as an instant bubble (with
      // optional editor-side effects), never sent to the model. Unhandled ones fall through.
      if (looksLikeCommand(prompt)) {
        try {
          const slash = await host.commandSlash(prompt, this.historyEntries());
          if (slash.handled) {
            const outcome = await this.applySlashEffects(slash.effects ?? []);
            if (outcome.chatPrompt !== null) {
              // sendAsPrompt (expanded user template): continue as a normal chat turn — refused like a typed question
              // when the backend is known to be down, its bubble taken back.
              if (await this.refusedWhileBackendDown(text, typed, question)) {
                return;
              }
              // The question IS the expanded prompt, as in the Visual Studio window: saved as "/name args", a reload
              // handed the model the template's name instead of what it had been asked.
              question.text = outcome.chatPrompt;
              this.hydrate();
              await this.chatTurn(outcome.chatPrompt, host);
              return;
            }
            question.notice = true;
            // A failed effect means the host's answer (e.g. "Switched to branch X") did not come true.
            const answer = [outcome.dropHostMarkdown ? '' : slash.markdown ?? '', ...outcome.notes]
              .filter((s) => s.length > 0)
              .join('\n\n');
            this.finishTurn(answer, null, false, 0);
            // /import's preview is a card with a box per item; its Markdown stays the notice's text — what a saved
            // conversation keeps, and what the agent protocol, which draws no card, shows.
            const last = this.transcript[this.transcript.length - 1];
            if (slash.importCard && last?.role === 'assistant') {
              last.importCard = { ...slash.importCard, id: `import-${++this.importCards}` };
              outcome.rehydrate = true;
            }
            if (outcome.rehydrate) {
              this.hydrate();
            }
            return;
          }
        } catch (err) {
          // A failed RPC is not "the host doesn't know this command" (that answer is
          // `handled: false`, handled above): it is the host refusing or erroring — typically
          // "a chat turn is already running". Falling through would send the raw `/branch 2`
          // to the model as a prompt, which is both useless and confusing.
          this.log(`[chat] command/slash failed: ${String(err)}`);
          question.notice = true;
          // The host is gone: no one waits for an approval card any more. A host that refused (its slot is held) still
          // waits for the cards of what holds it — denied here, a settings save's or a turn's question was answered "no".
          if (isConnectionLost(err)) {
            this.dismissAllPending();
          }
          this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
          return;
        }
      }

      await this.chatTurn(prompt, host);
    } finally {
      if (this.turnsEnded !== before && !this.busy) {
        this.afterTurn();
      }
    }
  }

  /**
   * Like the Visual Studio window: a message for the MODEL is refused before anything is consumed when the backend is
   * known to be down — no user bubble, nothing in the host's history, a typed text back in the input box. Checked again
   * first, so a backend that just came back is not refused on a stale badge. `question`: the bubble already shown for
   * a slash command whose turn turned out to need the model, taken back with the turn.
   */
  private async refusedWhileBackendDown(text: string, typed: boolean, question?: WvTranscriptItem): Promise<boolean> {
    if (this.status?.connected !== false) {
      return false;
    }
    await this.pollBackendStatus();
    if (this.status?.connected !== false) {
      return false;
    }
    if (question) {
      const at = this.transcript.indexOf(question);
      if (at >= 0) {
        this.transcript.splice(at, 1);
      }
      this.busy = false;
    }
    this.append({
      role: 'error',
      // A server that refused the check (a wrong API key) is running: "unreachable" would send the user to start it.
      // Only a typed text is back in the box: a card's or a button's gesture would have overwritten the draft there.
      text: this.status?.refused
        ? typed
          ? t('The backend refused the connection check — your message was not sent and is back in the input box. Check the API key and the URL in the settings.')
          : t('The backend refused the connection check — nothing was sent. Check the API key and the URL in the settings.')
        : typed
          ? t('The backend is unreachable — your message was not sent and is back in the input box.')
          : t('The backend is unreachable — nothing was sent.'),
      timestamp: ChatViewProvider.now(),
    });
    this.hydrate();
    this.giveBackTyped(text, typed);
    return true;
  }

  /**
   * A typed text refused before its turn goes back in the box the webview cleared; any other gesture's text does not.
   * ⚠ In front of what is typed there, never over it: a queued message is refused when the turn before it ends, and the
   * box then holds the draft typed meanwhile.
   */
  private giveBackTyped(text: string, typed: boolean): void {
    if (typed) {
      this.post({ type: 'restoreDraft', text });
    }
  }

  /** Applies the editor-side effects of a handled slash command. */
  private async applySlashEffects(
    effects: SlashEffect[],
  ): Promise<{ chatPrompt: string | null; notes: string[]; rehydrate: boolean; dropHostMarkdown: boolean }> {
    let chatPrompt: string | null = null;
    const notes: string[] = [];
    let rehydrate = false;
    let dropHostMarkdown = false;
    for (const e of effects) {
      switch (e.kind) {
        case 'sendAsPrompt':
          chatPrompt = e.value ?? null;
          break;
        case 'setPrompt':
          this.post({ type: 'setPrompt', text: e.value ?? '' });
          break;
        case 'attachChip':
          if (e.value) {
            const name = e.name ?? 'attachment';
            this.addChip(name, e.value);
            notes.push(t('📎 {0} attached to the next message.', name));
          }
          break;
        case 'copyToClipboard':
          await vscode.env.clipboard.writeText(e.value ?? '');
          break;
        case 'clearTranscript':
          // A new conversation archives the previous one first, like the reset button and the VS
          // window; the command bubble that asked for it is not part of the archive (as in branchAtTurn).
          const command = this.transcript[this.transcript.length - 1];
          if (command?.role === 'user' && looksLikeCommand(command.text)) {
            this.transcript.pop();
          }
          this.archiveConversation();
          this.clearTranscript();
          this.streamText = '';
          this.plan = null;
          this.promptTokens = 0;
          this.lastTokens = 0;
          this.sessionTokens = 0;
          this.sessionStart = null;
          rehydrate = true;
          break;
        case 'stateChange':
          // Bare /plan toggles the host's plan mode: the mode switch shows it.
          if (e.name === 'planMode') {
            this.planMode = e.value === 'on';
            this.post({ type: 'planMode', enabled: this.planMode });
          }
          if (e.name === 'model' && e.value) {
            this.model = e.value;
            this.sharedEcho.defaultModel = e.value;
            rehydrate = true;
            // The host has already switched and saved the model: a workspace settings file that refuses the
            // write only loses the per-workspace copy. Said next to the host's answer — thrown, it turned the
            // whole command into an error bubble.
            try {
              await vscode.workspace.getConfiguration('inferpal').update('model', e.value, vscode.ConfigurationTarget.Workspace);
            } catch (err) {
              this.log(`[chat] model setting not saved: ${String(err)}`);
              notes.push(t('Inferpal could not save this setting: {0}', ChatViewProvider.errorText(err)));
            }
          }
          break;
        case 'openSetup':
          // `/setup`: the first-run card, looking again.
          void this.setupFlow.start('command');
          break;
        case 'openSettings':
          // `/settings`: the settings panel, as the command palette opens it.
          try {
            await vscode.commands.executeCommand('inferpal.openSettings');
          } catch (err) {
            this.log(`[chat] openSettings effect failed: ${String(err)}`);
          }
          break;
        case 'configSaved':
          // The command saved Inferpal's settings (`/onboard apply`): the explicit VS Code copies follow, or the next host
          // start pushes the old utility model back; the chat re-reads its window, model and density.
          await followModelRouterSettings(this.getHost(), this.log);
          await this.configSaved();
          // An open settings panel reads its live blocks again, or after `/tools off` its Tools page offers "Turn off".
          SettingsPanel.onSavedElsewhere();
          break;
        case 'openFile':
          if (e.value) {
            try {
              await vscode.window.showTextDocument(vscode.Uri.file(e.value));
            } catch (err) {
              this.log(`[chat] openFile effect failed: ${String(err)}`);
            }
          }
          break;
        case 'exportRequest':
          void this.exportCommand();
          break;
        // /branch [args]: listed, checked and carried out on this transcript (see decideBranch).
        case 'branchCommand': {
          const [note, changed] = await this.decideBranch(e.value ?? '');
          notes.push(note);
          if (changed) {
            rehydrate = true;
          }
          break;
        }
        // /branch <n>: the host owns the fork but needs the full transcript (tool names and
        // timestamps live here, not in its API history).
        case 'branchRequest': {
          const [note, branched] = await this.branchAtTurn(Number(e.value));
          notes.push(note);
          if (branched) {
            rehydrate = true;
          }
          break;
        }
        // /branch <name>: switching branches is a plain session load (the host already
        // returned the localized bubble as markdown).
        case 'loadSession': {
          const [switched, failure] = await this.switchToSession(e.value ?? '');
          if (!switched) {
            notes.push(failure);
            dropHostMarkdown = true;
          }
          break;
        }
        default:
          break; // unknown kinds are ignored (forward compatibility)
      }
    }
    return { chatPrompt, notes, rehydrate, dropHostMarkdown };
  }

  /** /explain and /review: active selection (or whole document) fenced into the prompt,
   * answered as a normal streamed chat turn.
   *
   * ⚠ The code goes through the host's excerpt, sized from the configured context window. Fenced
   * whole, a file larger than the window is refused by LM Studio, and loses the HEAD of the request
   * with Ollama — the system prompt and this very instruction first — without a word; and the cut, when there is one, is named
   * under the question, where the Visual Studio window shows it on the attachment chip. */
  private async runExplainReview(kind: 'explain' | 'review', host: HostClient, question: WvTranscriptItem,
    request?: string): Promise<void> {
    const editor = this.getActiveEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      this.finishTurn('', t('Open a file in the editor to use /{0}.', kind), false, 0);
      return;
    }
    const doc = editor.document;
    const selection = !editor.selection.isEmpty;
    const code = selection ? doc.getText(editor.selection) : doc.getText();
    const file = vscode.workspace.asRelativePath(doc.uri);
    // The model the turn below will ask: the excerpt is sized for the window it really loaded.
    const excerpt = await host.codeExcerpt(code, file, selection, this.model || undefined, doc.uri.fsPath);
    // Excluded by the workspace's ignore files: nothing goes to the model, and the reason is the answer.
    if (excerpt.refusal) {
      this.finishTurn('', excerpt.refusal, false, 0);
      return;
    }
    const sentence = kind === 'explain'
      ? t('Explain the following code from {0} — what it does, how, and any pitfalls.', file)
      : t('Review the following code from {0}: point out bugs, risks, and concrete improvements.', file);
    // The user's own words after the command come with it (CodeActionRequest.InTheQuestion in the Core).
    const instruction = request ? `${sentence}\n\n${request}` : sentence;
    this.nameAttachmentsInQuestion([excerpt.label]);
    // The turn starts: the question enters the host's history, and a request the host refuses marks it again.
    question.notice = false;
    // A code action: answered from the code above, without tools — sent as an ordinary turn, the agent planned,
    // called tools and asked for approvals to explain a selection.
    await this.chatTurn(`${instruction}\n\n\`\`\`\n${excerpt.text}\n\`\`\``, host, true, instruction);
  }

  /** One model turn (agent or plain chat) with @-mention and pending-chip expansion. `codeAction`: a read-only
   * code action, answered by the code-actions model without tools. `query`: what the auto-context searches for —
   * the prompt as written by default, never with the attached files appended. */
  private async chatTurn(prompt: string, host: HostClient, codeAction = false, query?: string): Promise<void> {
    // When the request left: a long turn's end is announced (announceRunEnd); 0 = it never left.
    let startedAt = 0;
    try {
      const agentMode = vscode.workspace.getConfiguration('inferpal').get<boolean>('agentMode', false);
      const mentions = await this.expandMentions(prompt, host);
      let expanded = mentions.text;
      const attachedPaths = [...mentions.paths];
      // Under the question: the chips, and any typed @file that was cut or not sent.
      const named = [...mentions.labels];
      const chips = this.regenerating ? this.resendAttachments ?? [] : this.continuing ? [] : this.pendingAttachments;
      if (chips.length > 0) {
        // A chip of a file the workspace's ignore files exclude is not sent: asked once, for every chip with a file.
        const excluded = await ChatViewProvider.excluded(host, chips.map((a) => a.origin ?? ''));
        for (const [i, a] of chips.entries()) {
          const why = excluded[i];
          if (why) {
            expanded += `\n\n## Not attached: ${a.name}\n${why.forModel}`;
            named.push(why.notice);
            continue;
          }
          expanded += `\n\n## Attached: ${a.name}\n\`\`\`\n${a.content}\n\`\`\``;
          // The file's path, never the chip's label: the host resolves these against the root to skip the RAG
          // chunks of a file already in the prompt, and "📄 src/x.ts" names no file — the same content went in twice.
          if (a.sourcePath) {
            attachedPaths.push(a.sourcePath);
          }
        }
        named.push(...chips.filter((_, i) => !excluded[i]).map((a) => a.name));
        if (!this.regenerating) {
          this.pendingAttachments = [];
          this.postChips();
        }
      }
      const asked = this.lastQuestion();
      if (asked) {
        this.lastSent = { question: asked, text: prompt, codeAction, query, shown: asked.text, attachments: [...chips] };
      }
      // A regenerated question's notes come back under it, read: asked again with them, it is the question the history
      // keeps — and the next save keeps them too.
      const notes = this.regenerating ? this.resendNotes ?? [] : [];
      for (const text of notes) {
        const id = `note-${++this.noteSeq}`;
        this.append({ role: 'user', text, timestamp: ChatViewProvider.now(), note: 'read', noteId: id });
        this.post({ type: 'note', id, text, state: 'read' });
      }
      if (named.length > 0) {
        this.nameAttachmentsInQuestion(named);
      }
      // Nothing awaited between this check and the request: a Stop that came earlier ends the turn unsent.
      if (this.stopPressed) {
        this.endStoppedBeforeTheHost();
        return;
      }
      startedAt = Date.now();
      const result = await host.chatSend({
        prompt: expanded,
        model: this.model || undefined,
        agentMode,
        attachedPaths: attachedPaths.length > 0 ? attachedPaths : undefined,
        codeAction: codeAction || undefined,
        // The question, not the expanded prompt: searched with the attachments' bodies, the auto-context followed the
        // attached file instead of the question (Visual Studio searches the typed text).
        query: query ?? prompt,
        notes: notes.length > 0 ? notes : undefined,
      });
      // A stopped turn's partial answer is the host's: empty when nothing visible had come (reasoning only).
      const finalText = result.cancelled ? result.text : result.text || this.streamText;
      // The gauge shows what the NEXT question sends, as the X-Ray it opens does: the turn's own prompt measure
      // includes the agent run's internal transcript, which the next question does not carry.
      this.promptTokens = result.nextTurnTokens || result.promptTokens || this.promptTokens;
      if (result.error) {
        // ⚠ The question stays a question: an error the host ANSWERS comes after the question entered its history,
        // which keeps it (like the Visual Studio window), so Regenerate must take it back. Marked a notice, it was
        // resent on top of itself, and a reload dropped what the live history still held.
        this.keepFailedPartial(finalText);
        this.append({ role: 'error', text: result.error, timestamp: ChatViewProvider.now() });
      } else if (result.cancelled) {
        // Stopped before the question entered the host's history (during the context build): a notice, like a refused
        // request — Regenerate resends it without taking back the exchange before it, which the screen still shows.
        if (result.questionKept === false) {
          const asked = this.lastQuestion();
          if (asked) {
            asked.notice = true;
          }
        }
        // Like the VS window: a visible partial answer stays, an empty one is not saved, and the stop
        // is a lasting line — the webview's own note is gone after a reload.
        if (finalText.trim().length > 0) {
          this.append({ role: 'assistant', text: finalText, timestamp: ChatViewProvider.now() });
        }
        this.append({ role: 'error', text: t('Cancelled.'), timestamp: ChatViewProvider.now() });
      } else {
        this.append({
          role: 'assistant', text: finalText, timestamp: ChatViewProvider.now(),
          model: result.model ?? undefined, duration: result.duration ?? undefined, run: result.run ?? null,
        });
      }
      // The answer STAYS: this is added after it. Without it, a run cut short at its iteration
      // limit returned fluent text, indistinguishable from a task carried to its end - the fact
      // lived in OrchestratorResult and was read by nobody, on either side.
      if (!result.error && result.endNotice) {
        this.append({
          role: 'assistant', text: result.endNotice, timestamp: ChatViewProvider.now(), notice: true,
          continues: result.offersContinue === true,
        });
      }
      this.busy = false;
      this.lastTokens = result.tokensUsed;
      this.sessionTokens += result.tokensUsed;
      // The gauge measures against the window the host measured the turn against: the loaded one when
      // the server reports a smaller one than configured, or it shows room while requests are refused.
      if (result.contextWindow && result.contextWindow > 0) {
        this.contextWindow = result.contextWindow;
      }
      this.post({
        type: 'turnEnded',
        text: finalText,
        error: result.error ?? null,
        cancelled: result.cancelled,
        tokens: result.tokensUsed,
        promptTokens: this.promptTokens,
        timestamp: ChatViewProvider.now(),
        endNotice: result.endNotice ?? null,
        offersContinue: !result.error && result.offersContinue === true,
        contextWindow: this.contextWindow,
        run: result.run ?? null,
        duration: result.duration ?? null,
        model: result.model ?? null,
        canRegenerate: this.canRegenerate(),
      });
      const normal = !result.error && !result.cancelled;
      this.settleNotes(result.appliedNotes, normal);
      this.turnEnded(result.error ? 'failed' : result.cancelled ? 'stopped' : 'normal');
      this.announceRunEnd(result.announceEnd === true, result.error ? 'failed' : result.cancelled ? 'stopped' : 'normal',
                          [result.duration, result.run?.detail].filter((part) => !!part).join(' · '));
      void this.pollBackendStatus(); // the turn may have loaded a model — refresh the VRAM badge
    } catch (err) {
      // The request itself failed. Host gone (crashed or restarted): no one waits for these cards any more. Refused by a
      // host still there: the cards belong to what holds its slot, and wait.
      if (isConnectionLost(err)) {
        this.dismissAllPending();
      }
      const message = ChatViewProvider.errorText(err);
      // A REFUSED request (a turn already in flight, a host that died) never reached the host's history: its
      // question is a notice, so Regenerate does not take back the exchange before it and a restart does not replay it.
      const asked = this.lastQuestion();
      if (asked) {
        asked.notice = true;
      }
      this.keepFailedPartial(this.streamText);
      this.append({ role: 'error', text: message, timestamp: ChatViewProvider.now() });
      this.busy = false;
      this.post({
        type: 'turnEnded',
        text: this.streamText,
        error: message,
        cancelled: false,
        tokens: 0,
        promptTokens: this.promptTokens,
        timestamp: ChatViewProvider.now(),
        canRegenerate: this.canRegenerate(),
      });
      // No result: no note can be said read. Those drawn waiting go back to the input box.
      this.settleNotes(undefined, false);
      this.turnEnded('failed');
      // The host may be gone: the setting as last read decides.
      this.announceRunEnd(startedAt > 0 && this.runEndNotice && Date.now() - startedAt > LONG_RUN_SECONDS * 1000, 'failed', '');
    } finally {
      this.busy = false;
      // The webview removes the pause banner on stepResumed only: a turn cancelled while paused
      // (or whose host died) would otherwise keep a Resume button that resumes nothing.
      if (this.stepPaused) {
        this.stepPaused = false;
        this.post({ type: 'stepResumed' });
      }
      this.autoSaveLast();
      this.flushPendingModelPush();
      void this.refreshRunningWork();
    }
  }

  /** Pushes an assistant/error entry and closes the turn in the webview. */
  /** Upper bound on live transcript entries (see {@link append}). */
  private static readonly MAX_TRANSCRIPT = 2000;

  /**
   * Appends to the transcript, keeping the VIEW bounded. A long-lived session (tool bubbles carry the
   * full command output) would otherwise grow without limit in the webview and in every `hydrate`
   * payload. Trimming hides the oldest entries behind a marker; the saved session and the export keep
   * them ({@link wholeTranscript}).
   */
  /**
   * Answers the last question again, like the VS window: the previous exchange leaves the view and the
   * host's history before the question is resent. Resent on top, the old answer stayed in the
   * conversation and the model read it and the question twice. Reachability is checked before anything
   * is removed. A slash command that never reached the model has no exchange in the host to take back.
   */
  /** Set while Regenerate re-asks a question: the chips in the composer belong to the NEXT question, not to this one. */
  private regenerating = false;
  /** A Continue is being sent: it carries none of the chips waiting in the composer, as in Visual Studio. */
  private continuing = false;

  /**
   * Continue: the question "ask it to continue" would type, sent by the ordinary funnel — refused while the backend is
   * down or a turn runs (and said), saved like any turn. Replayed, not typed: no slash-command reading, no prompt
   * history, no composer chips; a code action's answer continues as one (its model, no tools), as Regenerate replays it.
   */
  private async continueTurn(): Promise<void> {
    if (this.continuing) {
      return;   // a second click while the first is on its way
    }
    const prompt = t('Continue.');
    this.continuing = true;
    try {
      await this.send(prompt, false, { prompt, codeAction: this.lastSent?.codeAction === true, query: prompt });
    } finally {
      this.continuing = false;
    }
  }

  /** A question meant for the model: anything typed, or a refused /explain or /review — never a command served
   *  without it (/note, /commit-exec, a code action), whose bubble is a notice that starts with its slash. */
  private static isModelQuestion(m: WvTranscriptItem): boolean {
    return m.role === 'user' && !m.note
      && (!m.notice || !looksLikeCommand(m.text) || /^\/(explain|review)\b/i.test(m.text));
  }

  /** Regenerate has a question to ask again — the button is offered only then: under a thread of notices alone (a
   *  backend down at start, a slash command's output), it was offered and did nothing. */
  private canRegenerate(): boolean {
    return this.transcript.some((m) => ChatViewProvider.isModelQuestion(m));
  }

  private async regenerate(): Promise<void> {
    // ⚠ The last question meant for the MODEL: the last user bubble may be a slash command shown as typed, and
    // resending it ran it again — a second /note, a /commit-exec repeated.
    const question = [...this.transcript].reverse().find((m) => ChatViewProvider.isModelQuestion(m));
    if (this.busy || !question) {
      return;
    }
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    if (this.status?.connected === false) {
      await this.pollBackendStatus();
      if (this.status?.connected === false) {
        this.append({
          role: 'error',
          text: this.status?.refused
            ? t('The backend refused the connection check — the last answer was kept and nothing was sent. Check the API key and the URL in the settings.')
            : t('The backend is unreachable — the last answer was kept and nothing was sent.'),
          timestamp: ChatViewProvider.now(),
        });
        this.hydrate();
        return;
      }
    }
    // ⚠ Taken back only if it is IN the host's history: the rollback removes the LAST question there, so for one
    // that never reached it (a command served without the model, a refused request) it removed the exchange BEFORE.
    // A turn that FAILED is in it: the host keeps the question of a failed turn, like the Visual Studio window.
    // A notice is exactly that, and the mark survives a reload — the identity of the last question sent did not,
    // so a reloaded /explain was resent on top of its own exchange.
    if (!question.notice) {
      try {
        await host.chatRollbackLastTurn();
      } catch (err) {
        // Refused (the host's slot is held): resending now would stack the question on the old exchange. Said — a click
        // that does nothing reads as a broken button.
        this.log(`[chat] regenerate refused: ${String(err)}`);
        void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
        return;
      }
    }
    const at = this.transcript.indexOf(question);
    if (this.busy || at < 0) {
      return;
    }
    // The notes its run read are part of the question in the host's history: asked again, it carries them.
    const notes = this.transcript.slice(at + 1).filter((m) => m.note === 'read').map((m) => m.text);
    this.transcript.splice(at);
    this.hydrate();
    // ⚠ The chips waiting in the composer were attached for the NEXT question: sent with this one, they left the
    // composer and went to the model with the old question. The question's OWN chips go again — resent from its
    // words alone, "explain this file" went without the file (Visual Studio resends `_lastSent`).
    const sent = this.lastSent?.question === question ? this.lastSent : undefined;
    this.regenerating = true;
    this.resendAttachments = sent?.attachments;
    this.resendNotes = notes;
    try {
      await this.send(sent?.shown ?? question.text, false,
                      sent && { prompt: sent.text, codeAction: sent.codeAction, query: sent.query });
    } finally {
      this.regenerating = false;
      this.resendAttachments = undefined;
      this.resendNotes = undefined;
    }
  }

  /**
   * Names the chips a turn consumed under its question, like the VS window's bubble. Their content
   * goes to the model only (an @-mention, by contrast, stays written in the question), so without the
   * names a reloaded session or an export shows "explain this" with nothing it refers to. The label,
   * never the content: a clipboard or problems snapshot describes a past state, and a file is re-read.
   */
  /**
   * A turn stopped before it reached the host: nothing was sent. Its question never entered the host's history, so it
   * is a notice (Regenerate resends it, with what it carried), and the stop is a lasting line, as for a sent turn.
   */
  private endStoppedBeforeTheHost(): void {
    const asked = this.lastQuestion();
    if (asked) {
      asked.notice = true;
    }
    this.append({ role: 'error', text: t('Cancelled.'), timestamp: ChatViewProvider.now() });
    this.busy = false;
    this.post({
      type: 'turnEnded',
      text: '',
      error: null,
      cancelled: true,
      tokens: 0,
      promptTokens: this.promptTokens,
      timestamp: ChatViewProvider.now(),
      canRegenerate: this.canRegenerate(),
    });
    this.settleNotes(undefined, false);
    this.turnEnded('stopped');
  }

  private nameAttachmentsInQuestion(names: readonly string[]): void {
    const question = this.lastQuestion();
    if (!question || names.length === 0) {
      return;
    }
    const recap = t('📎 Attached: {0}', names.join(' · '));
    question.text = question.text.length > 0 ? `${question.text}\n\n${recap}` : recap;
    // The bubble was drawn by turnStarted; nothing streams yet, so a rebuild costs no partial answer.
    this.hydrate();
  }

  /**
   * The visible part of a FAILED turn's answer stays, as a notice: drawn live, it was missing from the transcript — gone
   * at the next redraw, never saved — and Visual Studio kept it as an answer, which a reload handed to the model. A
   * notice is what both keep: on screen, never the model's answer.
   */
  private keepFailedPartial(partial: string): void {
    if (partial.trim().length > 0) {
      this.append({ role: 'assistant', text: partial, timestamp: ChatViewProvider.now(), notice: true });
    }
  }

  private append(item: WvTranscriptItem): void {
    this.transcript.push(item);
    this.trimTranscript();
  }

  /** Entries hidden from this view so far; while non-zero, `transcript[0]` is the marker. */
  private droppedEntries = 0;

  /** The entries hidden from the view, oldest first: still the conversation's, saved and exported with it. */
  private hiddenEntries: WvTranscriptItem[] = [];

  /**
   * Hides the oldest entries past the cap, behind a single marker that carries the running count.
   * The marker is replaced in place, never trimmed with the entries: dropping it restarted the count
   * at 1 on every append. The entries leave the view (and every `hydrate` payload), not the conversation.
   */
  private trimTranscript(): void {
    if (this.transcript.length <= ChatViewProvider.MAX_TRANSCRIPT + 1) {
      return;
    }
    const markerSlot = this.droppedEntries > 0 ? 1 : 0;
    const excess = this.transcript.length - ChatViewProvider.MAX_TRANSCRIPT - markerSlot;
    this.hiddenEntries.push(...this.transcript.splice(markerSlot, excess));
    this.droppedEntries += excess;
    const marker: WvTranscriptItem = {
      role: 'error',
      text: t('{0} older messages are hidden from this view to bound memory — they stay in the saved session and the export.', this.droppedEntries),
      timestamp: ChatViewProvider.now(),
    };
    if (markerSlot) {
      this.transcript[0] = marker;
    } else {
      this.transcript.unshift(marker);
    }
  }

  /** Readable text for a failed RPC: JSON-RPC errors carry the host's message. */
  private static errorText(err: unknown): string {
    return hostErrorText(err);
  }

  /**
   * The running host for a USER GESTURE — or undefined, having said why.
   *
   * ⚠ A gesture that returns in silence is indistinguishable from a broken product: the panel does
   * not open, the pin does not appear, the model pick does not apply, and nothing on screen
   * accounts for it. Background work (bootstrap, polling, pushing state on a change nobody asked
   * for) keeps the bare `isRunning` test — a toast there is noise, not information.
   */
  private hostForGesture(): HostClient | undefined {
    const host = this.getHost();
    if (host?.isRunning) {
      return host;
    }
    void vscode.window.showWarningMessage(hostUnavailableMessage());
    promptOpenFolder();
    return undefined;
  }

  /** Says a sentence once per channel, until {@link saidOnce} is cleared by a call that works. */
  private sayOnce(channel: string, message: string): void {
    if (this.saidOnce.has(channel)) {
      return;
    }
    this.saidOnce.add(channel);
    void vscode.window.showWarningMessage(message);
  }

  /** What a gesture that failed says: the line the log keeps, and the sentence the user reads. */
  private gestureFailed(what: string, err: unknown): void {
    this.log(`[chat] ${what} failed: ${String(err)}`);
    void vscode.window.showWarningMessage(ChatViewProvider.errorText(err));
  }

  private finishTurn(text: string, error: string | null, cancelled: boolean, tokens: number): void {
    const timestamp = ChatViewProvider.now();
    if (error) {
      this.append({ role: 'error', text: error, timestamp });
    } else if (text) {
      // A slash command's or a code action's result: shown as an assistant bubble, never an answer the model gave.
      this.append({ role: 'assistant', text, timestamp, notice: true });
    }
    this.busy = false;
    this.flushPendingModelPush();
    this.post({
      type: 'turnEnded',
      text: error ? '' : text,
      error,
      cancelled,
      tokens,
      promptTokens: this.promptTokens,
      timestamp,
      canRegenerate: this.canRegenerate(),
      // Drawn as kept: a notice, under no model's name. Drawn as an answer, it read live as the reply of the model
      // the header named — the one /import had just written, served or not — and as a notice once reloaded.
      notice: true,
    });
    this.settleNotes(undefined, !error && !cancelled);
    this.turnEnded(error ? 'failed' : cancelled ? 'stopped' : 'normal');
    this.autoSaveLast();
    void this.refreshRunningWork();   // a /task submitted, a docs source added: seen before a crash can take them
  }

  // ── In-place code actions (/fix /refactor /doc) ─────────────────────────────

  /** Editor context-menu entry point: routes the slash text through the normal send
   * pipeline (user bubble + interception), after revealing the chat so the outcome
   * bubble is actually seen. The bridge's last-active-editor cache keeps targeting
   * the right file even though revealing moves focus to the webview. */
  async runSlashCommand(text: string): Promise<void> {
    try {
      await vscode.commands.executeCommand('inferpal.chat.focus');
    } catch {
      // view can't be revealed (rare) — the action still runs, bubbles land on next open
    }
    // ⚠ A turn running: refused with the reason (send), never queued — the action reads the active editor and its
    // selection when it RUNS, and queued it would rewrite whatever is selected once the turn ends. Visual Studio queues
    // its context-menu actions because it takes their code at the click.
    await this.send(text);
  }

  /**
   * "Fix with Inferpal" from a quick fix: the Core's question, with the diagnostics and the code around each as its chip
   * — what Visual Studio's Error List sends — answered with the chat's tools: fixing is editing, under approvals.
   */
  fixProblems(problems: ProblemEntry[]): Promise<void> {
    return this.askWithChip((host) => host.problemsFix(problems, this.model || undefined), 'fix problems', false);
  }

  /**
   * "Analyze with Inferpal" on a debugger stop: the Core's question, with the stop and the code around its frames as its
   * chip — what Visual Studio's Debug menu sends — answered as a code action, without tools: nothing runs.
   */
  analyzeStop(): Promise<void> {
    // The model picked here: the stop is sized for the window of the model the code action asks.
    return this.askWithChip((host) => host.debugAnalyze(this.model || undefined), 'analyze the debugger stop', true);
  }

  /** The explorer's "Explain" on a file or a folder: the Core's question, as a code action — without tools. */
  explainItem(uri: vscode.Uri): Promise<void> {
    return this.askWithChip((host) => host.explorerExplain(uri.fsPath, this.model || undefined), 'explain', true);
  }

  /** The Test view's "Make This Test Pass": `/tdd` with the test's filter, as if typed. */
  fixTest(item: vscode.TestItem): Promise<void> {
    return this.runSlashCommand(`/tdd ${testFilter(item)}`);
  }

  /**
   * The Test view's "Explain the Failure": with the failure message the view shows, or — from the test itself — the host
   * runs it, which takes the test's time: said while it runs, and the person can cancel it. A code action, without tools.
   */
  explainTestFailure(item: vscode.TestItem, message?: vscode.TestMessage): Promise<void> {
    const params = {
      label: item.label,
      filter: testFilter(item),
      failure: message ? failureText(message) : undefined,
      file: item.uri?.scheme === 'file' ? item.uri.fsPath : undefined,
      line: item.range ? item.range.start.line + 1 : undefined,
      // Where the failure points: the failing line, often past the window around the test's first line.
      failureFile: message?.location?.uri.scheme === 'file' ? message.location.uri.fsPath : undefined,
      failureLine: message?.location ? message.location.range.start.line + 1 : undefined,
      model: this.model || undefined,
    };
    return this.askWithChip((host) => message
      ? host.testsExplain(params)
      : ChatViewProvider.whileShown(t('Running {0}…', item.label), (token) => host.testsExplain(params, token)),
    'explain the test failure', true);
  }

  /**
   * A host call behind a cancellable notification. Cancelled by the person: `undefined`, whether the host stopped or had
   * already answered — a reply that crosses the cancellation is never acted on.
   */
  private static whileShown<T>(title: string, call: (token: vscode.CancellationToken) => Promise<T>): Promise<T | undefined> {
    return Promise.resolve(vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title, cancellable: true },
      (_progress, token) => call(token).then(
        (result) => (token.isCancellationRequested ? undefined : result),
        (err: unknown) => {
          if (token.isCancellationRequested) {
            return undefined;
          }
          throw err;
        })));
  }

  /**
   * Source Control's "Propose a Commit Message": `/commit`'s proposal, written by the host for that repository, put in
   * its commit message box — nothing is committed, the button stays the person's. A message already typed is replaced
   * only with their agreement, asked once the proposal is there (they may have typed meanwhile).
   */
  async proposeCommitMessage(sourceControl?: { rootUri?: vscode.Uri }): Promise<void> {
    const api = await gitApi().catch((err: unknown) => {
      this.log(`[chat] Git extension unavailable: ${String(err)}`);
      return undefined;
    });
    const repo = api && repositoryFor(api, sourceControl?.rootUri);
    if (repo === 'several') {
      void vscode.window.showWarningMessage(
        t('Several Git repositories are open: choose one in the Source Control view, then propose from its menu.'));
      return;
    }
    if (repo === 'unknown') {
      void vscode.window.showWarningMessage(
        t('The Git extension has no open repository at {0}: refresh the Source Control view, then try again.',
          sourceControl?.rootUri?.fsPath ?? ''));
      return;
    }
    if (!repo) {
      void vscode.window.showWarningMessage(t('No Git repository found: open a folder under Git, with the Git extension enabled.'));
      return;
    }
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    // The box as it was at the click: emptied by the time the proposal arrives, it was most likely committed — the
    // proposal would then describe changes already in history.
    const before = repo.inputBox.value;
    let proposal: CommitMessageProposal | undefined;
    try {
      proposal = await ChatViewProvider.whileShown(t('Writing a commit message…'),
        (token) => host.proposeCommitMessage(repo.rootUri.fsPath, token));
    } catch (err) {
      this.gestureFailed('propose a commit message', err);
      return;
    }
    if (!proposal) {
      return;
    }
    if (!proposal.message) {
      // The refusal, and what the host noted on the way (a diff cut, files left out): both said.
      void vscode.window.showWarningMessage([proposal.refusal, proposal.notice].filter(Boolean).join('\n\n'));
      return;
    }
    const typed = repo.inputBox.value.trim();
    if (!typed && before.trim()) {
      void vscode.window.showWarningMessage(
        t('The commit message box was emptied while the message was written — committed meanwhile? Propose again.'));
      return;
    }
    if (typed && typed !== proposal.message.trim()) {
      // Modal, the proposal shown: nothing is typed into the box while the question is open, and the choice is made
      // knowing what would replace the message.
      const replace = t('Replace');
      const answer = await vscode.window.showWarningMessage(
        t('The commit message box already holds a message. Replace it with the proposal?'),
        { modal: true, detail: proposal.message }, replace);
      if (answer !== replace) {
        return;
      }
    }
    repo.inputBox.value = proposal.message;
    if (proposal.notice) {
      // A cut diff or reply, files left undescribed: a warning; "nothing staged" alone is information.
      void (proposal.notice.includes('⚠')
        ? vscode.window.showWarningMessage(proposal.notice)
        : vscode.window.showInformationMessage(proposal.notice));
    }
  }

  /** The explorer's "Review Changes": `/check` on that file or folder, as if typed. */
  reviewItem(uri: vscode.Uri): Promise<void> {
    return this.runSlashCommand(`/check "${uri.fsPath}"`);
  }

  /**
   * The explorer's "Attach to Chat": the chip `@folder` or the attach button makes, in the composer, nothing sent. A
   * folder goes through the host (its refusals); a file is the editor's text, judged against the ignore files when sent.
   */
  async attachItem(uri: vscode.Uri): Promise<void> {
    try {
      await vscode.commands.executeCommand('inferpal.chat.focus');
    } catch {
      // view can't be revealed (rare) — the chip still lands, shown on next open
    }
    try {
      if ((await vscode.workspace.fs.stat(uri)).type & vscode.FileType.Directory) {
        await this.resolveMention('folder', uri.fsPath);
        return;
      }
      const doc = await vscode.workspace.openTextDocument(uri);
      this.addChip('📄 ' + vscode.workspace.asRelativePath(uri), doc.getText(), uri.fsPath, uri.fsPath);
    } catch (err) {
      // A binary or unreadable file: the user picked it, and no chip appearing reads as a broken menu.
      this.gestureFailed('attach to chat', err);
    }
  }

  /**
   * A gesture's question built by the host, its attachment as a chip. Refused during a turn, like the build banner's Fix
   * them: the chip would ride the next question. A build that gives nothing was cancelled by the person: nothing is said.
   */
  private async askWithChip(build: (host: HostClient) => Promise<GestureQuestion | undefined>, what: string,
    codeAction: boolean): Promise<void> {
    try {
      await vscode.commands.executeCommand('inferpal.chat.focus');
    } catch {
      // view can't be revealed (rare) — the question still goes, bubbles land on next open
    }
    if (this.busy) {
      void vscode.window.showWarningMessage(t('A turn is still running — wait for it to finish or stop it, then try again.'));
      return;
    }
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    let ask: GestureQuestion | undefined;
    try {
      ask = await build(host);
    } catch (err) {
      this.gestureFailed(what, err);
      return;
    }
    if (!ask) {
      return;
    }
    if (ask.refusal) {
      void vscode.window.showWarningMessage(ask.refusal);
      return;
    }
    // A turn may have started while the host built the attachment: the chip would ride it, or the next question.
    if (this.busy) {
      void vscode.window.showWarningMessage(t('A turn is still running — wait for it to finish or stop it, then try again.'));
      return;
    }
    // The gesture's chip alone goes with its question: the draft's chips are the draft's, set aside and given back, as
    // Visual Studio keeps them. Found-while-typing chips go, as with any gesture's message.
    const draft = this.pendingAttachments.filter((a) => !a.auto);
    this.pendingAttachments = [];
    this.addChip(ask.chip, ask.content);
    const chip = this.pendingAttachments[0];
    try {
      // A code action goes through the turn a regenerated one replays (its prompt, its flag): the code-actions model,
      // without tools.
      await (codeAction ? this.send(ask.question, false, { prompt: ask.question, codeAction: true, query: ask.question })
        : this.send(ask.question));
    } finally {
      // Refused (the backend down, a turn begun meanwhile): the chip goes with the question, never with the next one.
      this.pendingAttachments = [...draft, ...this.pendingAttachments.filter((a) => a !== chip)];
      this.postChips();
    }
  }

  /**
   * What the user typed after a code-action command (`/fix the off-by-one in the loop`), or undefined — given to the
   * model with the action (`CodeActionRequest` in the Core). Dropped, the action answered another question than the
   * one asked.
   */
  private static requestOf(prompt: string): string | undefined {
    const rest = prompt.trim().replace(/^\S+\s*/, '').trim();
    return rest.length > 0 ? rest : undefined;
  }

  /** The in-place action a prompt requests, or undefined (first token match, VS parity). */
  private static codeActionKind(prompt: string): 'fix' | 'refactor' | 'doc' | undefined {
    switch (prompt.split(/\s+/, 1)[0].toLowerCase()) {
      case '/fix':
        return 'fix';
      case '/refactor':
        return 'refactor';
      case '/doc':
        return 'doc';
      default:
        return undefined;
    }
  }

  /**
   * VS Code side of the inline diff preview: the host runs the model step
   * (`codeAction/run`) and returns per-hunk offset edits; they are shown in the native
   * Refactor Preview (one confirmable entry per hunk — the ✓/✗ of the VS adornment).
   * Preview disabled (`inlineDiffPreviewEnabled: false`) ⇒ direct apply, one undo step.
   * Comfort only, never a control: these paths never went through approvals (VS parity).
   */
  private async runCodeAction(kind: 'fix' | 'refactor' | 'doc', host: HostClient, request?: string): Promise<void> {
    const finish = (role: 'assistant' | 'error', text: string) =>
      this.finishTurn(role === 'error' ? '' : text, role === 'error' ? text : null, false, 0);

    const editor = this.getActiveEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      finish('error', t('Open a file in the editor to use /{0}.', kind));
      return;
    }

    const document = editor.document;
    const before = document.getText();
    this.post({ type: 'status', text: t('Running /{0} on {1}…', kind, vscode.workspace.asRelativePath(document.uri)) });

    let result: CodeActionResult;
    try {
      result = await host.codeActionRun({
        kind,
        text: before,
        selStart: document.offsetAt(editor.selection.start),
        selEnd: document.offsetAt(editor.selection.end),
        model: this.model || undefined,
        path: document.uri.fsPath,
        request,
      });
    } catch (err) {
      finish('error', ChatViewProvider.errorText(err));
      return;
    }

    if (result.outcome === 'cancelled') {
      // Stopped: nothing is offered or applied, and the stop is a lasting line, as for a stopped chat turn.
      this.append({ role: 'error', text: t('Cancelled.'), timestamp: ChatViewProvider.now() });
      this.finishTurn('', null, true, 0);
      return;
    }
    if (result.outcome === 'noChange') {
      finish('assistant', t('Nothing to change — the code already looks good.'));
      return;
    }
    if (result.outcome === 'refused') {
      finish('error', result.failureDetail ?? '');
      return;
    }
    if (result.outcome !== 'edited' || result.edits.length === 0) {
      const base = t('The code action failed — check the backend connection and the model.');
      finish('error', result.failureDetail ? `${base}\n\n${result.failureDetail}` : base);
      return;
    }
    // The offsets were computed against the text we sent; a buffer that moved meanwhile
    // would misplace every hunk (same freshness guard as the VS renderer).
    if (document.getText() !== before) {
      finish('error', t('The document changed while the model was working — run /{0} again.', kind));
      return;
    }

    const preview = await this.inlineDiffPreviewEnabled(host);
    const applied = await this.applyRewrite(document, result.edits, preview, (index) => t('Inferpal /{0} — change {1}', kind, index));
    if (!applied) {
      finish('assistant', t('Rewrite discarded — no changes were applied.'));
    } else if (preview) {
      finish('assistant', t('Rewrite applied from the preview — undo with Ctrl+Z if needed.'));
    } else {
      finish('assistant', t('Rewrite applied — undo with Ctrl+Z if needed.'));
    }
  }

  /** A rewrite's per-hunk edits, in the native Refactor Preview (one confirmable entry per hunk) or applied at once with
   *  the preview off — one undo step either way. True when applied; false when discarded, or nothing left checked.
   *  `inTurn`: the rewrite ends a chat turn, so the chat's Stop discards its preview; Edit with AI's, outside the chat,
   *  is never touched by it. */
  private async applyRewrite(
    document: vscode.TextDocument, edits: CodeActionResult['edits'], preview: boolean, label: (index: number) => string,
    inTurn = true,
  ): Promise<boolean> {
    const edit = new vscode.WorkspaceEdit();
    for (const e of edits) {
      const range = new vscode.Range(document.positionAt(e.start), document.positionAt(e.end));
      if (preview) {
        edit.replace(document.uri, range, e.newText, { needsConfirmation: true, label: label(e.index) });
      } else {
        edit.replace(document.uri, range, e.newText);
      }
    }
    // With confirmable entries this opens the Refactor Preview and resolves on the user's decision.
    if (!inTurn) {
      return vscode.workspace.applyEdit(edit, { isRefactoring: true });
    }
    this.codeActionPreviewOpen = preview;
    try {
      return await vscode.workspace.applyEdit(edit, { isRefactoring: true });
    } finally {
      this.codeActionPreviewOpen = false;
    }
  }

  /**
   * Edit with AI: an instruction for the selection — or the caret's line — and the edit shown in the native Refactor
   * Preview, outside the chat, as Visual Studio's command does. The host builds the prompt (the Core's, the one Visual
   * Studio sends) and picks the inline-edit model; Stop is the notification's Cancel.
   */
  async editWithAi(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.uri.scheme !== 'file') {
      void vscode.window.showInformationMessage(t('Open a file in the editor to use Edit with AI.'));
      return;
    }
    const host = this.hostForGesture();
    if (!host) {
      return;
    }
    const instruction = (await vscode.window.showInputBox({
      title: t('Edit with AI'),
      prompt: t('What should change in the selected code, or in the current line?'),
      placeHolder: t('e.g. use a dictionary instead of the switch'),
      ignoreFocusOut: true,
    }))?.trim();
    if (!instruction) {
      return;   // Escape: the person's own cancel
    }

    const document = editor.document;
    const before = document.getText();
    let result: CodeActionResult;
    let cancel: vscode.CancellationToken | undefined;
    try {
      result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: t('Inferpal is editing the code…'), cancellable: true },
        (_progress, token) => (cancel = token, host.codeActionRun({
          kind: 'edit',
          text: before,
          selStart: document.offsetAt(editor.selection.start),
          selEnd: document.offsetAt(editor.selection.end),
          model: this.model || undefined,
          path: document.uri.fsPath,
          request: instruction,
        }, token)));
    } catch (err) {
      // The notification's Cancel comes back as the request's cancellation error: the person's own stop, nothing to say.
      if (!cancel?.isCancellationRequested) {
        this.gestureFailed('edit with AI', err);
      }
      return;
    }

    if (result.outcome === 'cancelled') {
      return;
    }
    if (result.outcome === 'noChange') {
      void vscode.window.showInformationMessage(t('Nothing to change — the code already looks good.'));
      return;
    }
    if (result.outcome !== 'edited' || result.edits.length === 0) {
      const base = t('The code action failed — check the backend connection and the model.');
      void vscode.window.showWarningMessage(
        result.outcome === 'refused' ? result.failureDetail ?? base : result.failureDetail ? `${base}\n\n${result.failureDetail}` : base);
      return;
    }
    // The offsets were computed against the text sent: a buffer that moved meanwhile would misplace every hunk.
    if (document.getText() !== before) {
      void vscode.window.showWarningMessage(t('The document changed while the model was working — run Edit with AI again.'));
      return;
    }
    const preview = await this.inlineDiffPreviewEnabled(host);
    if (!(await this.applyRewrite(document, result.edits, preview, (index) => t('Inferpal Edit with AI — change {0}', index), false))) {
      void vscode.window.showInformationMessage(t('Rewrite discarded — no changes were applied.'));
    }
  }

  /**
   * /test: the tests of the selection — or of the whole file — planned by the host, and written here by an edit the
   * editor can undo: an existing test file replaced in its buffer (unsaved tests included in what the model extends),
   * a new one created. Visual Studio writes them the same way.
   */
  private async runTestGeneration(host: HostClient, request?: string): Promise<void> {
    const editor = this.getActiveEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      this.finishTurn('', t('Open a file in the editor to use /{0}.', 'test'), false, 0);
      return;
    }
    const document = editor.document;
    // A selection of blanks tests nothing: the file is the input then, as with no selection.
    const selected = editor.selection.isEmpty ? '' : document.getText(editor.selection);
    const code = selected.trim() ? selected : document.getText();
    this.post({ type: 'status', text: t('Running /{0} on {1}…', 'test', vscode.workspace.asRelativePath(document.uri)) });
    // The open buffers as they are now: a test file edited while the model works is not replaced under the edit.
    const versions = new Map(vscode.workspace.textDocuments.map((d) => [d.uri.fsPath, d.version] as const));

    let result: TestsGenerateResult;
    try {
      result = await host.testsGenerate({ path: document.uri.fsPath, code, request });
    } catch (err) {
      this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
      return;
    }
    if (result.cancelled) {
      this.append({ role: 'error', text: t('Cancelled.'), timestamp: ChatViewProvider.now() });
      this.finishTurn('', null, true, 0);
      return;
    }
    if (!result.testPath || result.content == null) {
      this.finishTurn(result.message ?? '', null, false, 0);   // nothing to write: the host says why
      return;
    }

    const uri = vscode.Uri.file(result.testPath);
    const edit = new vscode.WorkspaceEdit();
    if (result.extended) {
      const tests = await vscode.workspace.openTextDocument(uri);
      const seen = versions.get(tests.uri.fsPath);
      if (seen !== undefined && seen !== tests.version) {
        this.finishTurn('', t('The test file changed while the model was working — run /test again.'), false, 0);
        return;
      }
      edit.replace(uri, new vscode.Range(tests.positionAt(0), tests.positionAt(tests.getText().length)), result.content);
    } else {
      // Planned as a new file: one that appeared meanwhile is not written into.
      if (await vscode.workspace.fs.stat(uri).then(() => true, () => false)) {
        this.finishTurn('', t('The test file changed while the model was working — run /test again.'), false, 0);
        return;
      }
      edit.createFile(uri, { ignoreIfExists: true });
      edit.insert(uri, new vscode.Position(0, 0), result.content);
    }
    if (!(await vscode.workspace.applyEdit(edit))) {
      this.finishTurn('', t('The test file could not be written — nothing was changed.'), false, 0);
      return;
    }
    try {
      await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(uri), { preview: false });
    } catch (err) {
      this.log(`[chat] /test: the test file could not be shown: ${String(err)}`);   // written all the same
    }
    this.finishTurn(result.message ?? '', null, false, 0);
  }

  /** Host-side config gate of the preview (camelCase JSON); defaults on, like VS. */
  private async inlineDiffPreviewEnabled(host: HostClient): Promise<boolean> {
    try {
      const cfg = JSON.parse(await host.configGet()) as { inlineDiffPreviewEnabled?: boolean };
      return cfg.inlineDiffPreviewEnabled !== false;
    } catch {
      return true; // unreadable config — preview is the safe, non-destructive default
    }
  }

  // ── Extension → webview ─────────────────────────────────────────────────────

  private hydrate(): void {
    // No running host (e.g. no workspace folder) → surface it in the connection badge
    // instead of an indistinct empty header.
    const status = this.status ?? (this.getHost()?.isRunning ? null : ChatViewProvider.noHostStatus());
    this.post({
      type: 'hydrate',
      transcript: this.transcript,
      models: this.models,
      model: this.model,
      busy: this.busy,
      canRegenerate: this.canRegenerate(),
      stream: this.streamText,
      status,
      commands: this.commands,
      agentMode: vscode.workspace.getConfiguration('inferpal').get<boolean>('agentMode', false),
      contextWindow: this.contextWindow,
      promptTokens: this.promptTokens,
      lastTokens: this.lastTokens,
      plan: this.busy ? this.plan : null,
      history: this.historyEntries(),
      toolBubblesExpanded: this.toolBubblesExpanded,
      compact: this.compact,
      mentionCategories: this.mentionCats,
      chips: this.chipsShown(),
      pins: this.pins,
      planMode: this.planMode,
      ...this.editorContext(),
      attachedRecap: t('📎 Attached: {0}'),
      queue: this.queuedPrompts.map((q) => q.text),
    });
    // The transcript carries no card and the webview just rebuilt from it: a card still waiting
    // would vanish while the host keeps waiting for its answer, with no timeout.
    for (const [id, pending] of this.pendingApprovals) {
      this.post({ type: 'approval', id, message: pending.message, card: pending.card });
    }
    for (const pending of this.pendingInputs.values()) {
      this.post({ type: 'inputForm', card: pending.card });
    }
    if (this.busy && this.stepPaused) {
      this.post({ type: 'stepPaused' });
    }
  }

  private post(message: unknown): void {
    void this.view?.webview.postMessage(message);
  }

  /** The file open in the editor and the errors of the Problems panel — the welcome screen's cards and banner. */
  private editorContext(): { editorFile: string | null; problems: number } {
    const editor = this.getActiveEditor();
    const file = editor && editor.document.uri.scheme === 'file'
      ? editor.document.uri.path.split('/').pop() ?? null
      : null;
    // The errors as the build banner counts them: once each.
    return { editorFile: file, problems: panelErrors().length };
  }

  /**
   * Build tasks that end, as Visual Studio's "Build Failed" banner follows its builds: a failure draws the banner over
   * the chat, a build that passes takes it down. A task stopped before it exited (no exit code) says nothing either way,
   * nor does a watch task (`isBackground`): stopped, it ends with a code, and it was not a build that failed.
   * The banner reads the Problems panel as it stands — again at each change of it (the task's problem matcher may fill it
   * after the process ends, a fix may empty it) —, never a count frozen when the build failed.
   */
  private watchBuilds(): void {
    this.context.subscriptions.push(vscode.tasks.onDidEndTaskProcess((e) => {
      const task = e.execution.task;
      if (e.exitCode === undefined || task.isBackground || !isBuildTask(task)) {
        return;
      }
      // A build that passes takes down the banner of ITS failure: another build task passing (build-docs) says nothing
      // of the project that still does not compile.
      if (e.exitCode === 0) {
        if (this.failedBuild === task.name) {
          ++this.buildSeq;
          this.failedBuild = null;
          this.post({ type: 'buildBanner', banner: null });
        }
        return;
      }
      const seq = ++this.buildSeq;
      setTimeout(() => {
        if (seq === this.buildSeq) {
          this.failedBuild = task.name;
          this.postBuildBanner();
        }
      }, 500);
    }));
  }

  /** The banner of the build that failed, read from the Problems panel now. */
  private postBuildBanner(): void {
    const banner: WvBuildBanner | null = this.failedBuild === null ? null : { seq: this.buildSeq, ...buildBannerOf(this.failedBuild) };
    this.post({ type: 'buildBanner', banner });
  }

  /** Follows the editor for the welcome screen; debounced, the Problems panel changes on every keystroke. */
  private watchEditorContext(): void {
    if (this.editorWatch) {
      return;
    }
    this.editorWatch = true;
    const push = (): void => {
      if (this.editorContextTimer) {
        clearTimeout(this.editorContextTimer);
      }
      this.editorContextTimer = setTimeout(() => {
        const c = this.editorContext();
        this.post({ type: 'editorContext', file: c.editorFile, problems: c.problems });
        if (this.failedBuild !== null) {
          this.postBuildBanner();
        }
      }, 400);
    };
    this.context.subscriptions.push(
      vscode.window.onDidChangeActiveTextEditor(push),
      vscode.languages.onDidChangeDiagnostics(push),
    );
  }

  /** The mode switch: agent mode is a setting, plan mode is the host's — Plan is agent mode, read-only. */
  private async setMode(mode: 'chat' | 'agent' | 'plan'): Promise<void> {
    const agent = mode !== 'chat';
    const config = vscode.workspace.getConfiguration('inferpal');
    if (config.get<boolean>('agentMode', false) !== agent) {
      try {
        await config.update('agentMode', agent, vscode.ConfigurationTarget.Workspace);
      } catch (err) {
        // The mode is read from the settings on every turn: unsaved, it did not change — the switch stays.
        this.log(`[chat] agent mode not saved: ${String(err)}`);
        void vscode.window.showWarningMessage(t('Inferpal could not save this setting: {0}', ChatViewProvider.errorText(err)));
        this.post({ type: 'agentMode', enabled: !agent });
        return;
      }
      this.post({ type: 'agentMode', enabled: agent });
      await this.pushAgentModeToHost(agent);
    }
    const plan = mode === 'plan';
    if (this.planMode !== plan) {
      const host = this.hostForGesture();
      if (!host) {
        this.post({ type: 'planMode', enabled: this.planMode });
        return;
      }
      try {
        this.planMode = await host.planMode(plan);
      } catch (err) {
        this.gestureFailed('plan/mode', err);
      }
      this.post({ type: 'planMode', enabled: this.planMode });
    }
  }

  private static now(): string {
    return new Date().toLocaleTimeString(vscode.env.language, { hour: '2-digit', minute: '2-digit' });
  }

  /** Localized strings injected into the webview (window.__l10n). Templates keep their
   * {0} placeholders — substitution happens webview-side (t()). */
}
