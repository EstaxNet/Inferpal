// Spawns and supervises the Inferpal.Host process and exposes its JSON-RPC surface
// as a typed API. Deliberately free of any 'vscode' import so it can be smoke-tested
// with plain Node against the real host binary.
import * as cp from 'child_process';
import * as rpc from 'vscode-jsonrpc/node';
import type { CancellationToken } from 'vscode-jsonrpc';
import type { DebugDelegate } from './debugBridge';
import {
  ActiveDocumentDto,
  ApprovalNote,
  DebugBreakpointParams,
  DebugCaptureTestParams,
  DebugEvaluateParams,
  DebugStepParams,
  BackendStatusResult,
  ChatSendParams,
  ChatSendResult,
  CodeActionParams,
  CodeActionResult,
  ChatExportParams,
  ConnectionCheckResult,
  DocumentParams,
  EditResultDto,
  FimSettingsResult,
  IndexStatusResult,
  InitializeParams,
  InitializeResult,
  MentionCategory,
  MentionItem,
  MentionResolveResult,
  PlanNotice,
  SavedMessage,
  SessionBranchCommandResult,
  SessionBranchResult,
  SettingsSchema,
  SessionLoadResult,
  SessionTitleResult,
  SessionSummary,
  SlashCommandInfo,
  SlashCommandResult,
  StepUpdateNotice,
  TextNote,
  ThinkingNote,
  ToolNotice,
  XRayPanel,
  ConfigUpdateResult,
} from './protocol';

/** Reverse-RPC surface the editor side must provide before `start()`. */
export interface EditorDelegate {
  /** The token relays the host's `$/cancelRequest` (turn cancelled) — honour it: §27.5. */
  approvalRequest(note: ApprovalNote, token?: CancellationToken): Promise<number>;
  activeDocument(): Promise<ActiveDocumentDto>;
  insertAtCursor(text: string): Promise<string | null>;
  replaceSelection(text: string): Promise<EditResultDto>;
  /** Formatted Problems-panel diagnostics, or null when clean (host builds instead). */
  editorDiagnostics(): Promise<string | null>;
  /** Stores an opaque payload in the editor's secret store and echoes back what to keep. */
  protectSecret(key: string, value: string): Promise<string>;
  /** Reverse of {@link protectSecret}. Throws when the payload is unknown or unreadable. */
  unprotectSecret(key: string, value: string): Promise<string>;
}

/** Streamed chat events (host notifications) fanned out to the UI. */
export interface ChatEvents {
  onToken?(text: string): void;
  /** `text` is the throttled rolling tail of the model's reasoning on the agent path, and null in
   *  plain chat - where surfacing a reasoning model's whole deliberation reads as stray output.
   *  The Core decides which; this is only the wire. */
  onThinking?(text: string | null): void;
  onStep?(text: string): void;
  onPlan?(plan: PlanNotice): void;
  onStepUpdate?(update: StepUpdateNotice): void;
  onTool?(tool: ToolNotice): void;
  onStreamReset?(): void;
  onStepPaused?(): void;
  onStepResumed?(): void;
  /** A background `/task` reached a terminal state — rendered as a persistent bubble, like VS. */
  onTaskFinished?(text: string): void;
}

export interface HostClientOptions {
  /** Full path to Inferpal.Host(.exe). */
  hostPath: string;
  /** Workspace root passed to `initialize`. */
  rootDir: string;
  /** Editor display language (e.g. vscode.env.language). */
  locale?: string;
  clientName?: string;
  /** Receives host stderr lines and lifecycle messages (→ output channel). */
  log?(line: string): void;
  /** Called when the host process dies without a `stop()` call. */
  onCrash?(exitCode: number | null): void;
}

export class HostClient {
  private proc: cp.ChildProcess | undefined;
  private conn: rpc.MessageConnection | undefined;
  private events: ChatEvents = {};
  private stopping = false;
  /** True from spawn until the `initialize` handshake answers: a crash then is surfaced
   * by `start()` rejecting (and possibly retried), not by the `onCrash` popup. */
  private starting = false;
  /** Set when stderr carries the .NET "Couldn't find a valid ICU package" FailFast. */
  private icuCrash = false;
  /** Why the process could not be run at all (EACCES, ENOENT) — reported instead of a dead pipe. */
  private spawnError: Error | undefined;
  /** Resolves when the current process closed (stdio flushed) — the ICU message can
   * land on stderr after the RPC pipe already broke, so `start()` waits on this
   * before deciding whether the crash was the libicu case. */
  private closed: Promise<void> | undefined;

  /** Host handshake answer, available after start(). */
  public info: InitializeResult | undefined;

  /** True while a chat turn is in flight. FIM must skip then: the agent run holds
   * the host-side GPU lease for its whole duration and the request would just queue. */
  public get isChatBusy(): boolean {
    return this.chatBusyDepth > 0;
  }

  /**
   * How many GPU-holding requests are in flight.
   *
   * ⚠ A boolean was wrong, and silently: THREE entry points raise it (`chat/send`,
   * `command/slash`, `codeAction/run`) and they overlap in ordinary use — a `/tdd` typed while a
   * turn streams, a code action launched from the editor during either. The first `finally` then
   * cleared the flag while the other request still held the lease, FIM stopped skipping, and its
   * requests queued behind the busy GPU only to be dropped. Counting is the fix; taking and
   * releasing through the single funnel below is what keeps it counted.
   */
  private chatBusyDepth = 0;

  /** The one place the busy count is taken and released — see {@link chatBusyDepth}. */
  private async whileChatBusy<T>(run: () => Promise<T>): Promise<T> {
    this.chatBusyDepth++;
    try {
      return await run();
    } finally {
      // Never below zero: a stray release must not make a live turn look idle.
      this.chatBusyDepth = Math.max(0, this.chatBusyDepth - 1);
    }
  }

  constructor(
    private readonly options: HostClientOptions,
    private readonly delegate: EditorDelegate,
    /**
     * Optional debugger surface (roadmap §21). Its presence is what the host is told in the
     * handshake, and what decides whether `debug_control` / `debug_inspect` exist for the model at
     * all — an editor that cannot drive a debugger must not advertise two tools that always fail.
     */
    private readonly debugDelegate?: DebugDelegate,
  ) {}

  get isRunning(): boolean {
    return this.conn !== undefined;
  }

  /** The workspace root this host was started against. */
  get rootDir(): string {
    return this.options.rootDir;
  }

  /** Replaces the streamed-chat event sinks (the chat view re-registers on resolve). */
  setChatEvents(events: ChatEvents): void {
    this.events = events;
  }

  /**
   * Spawns the host, wires reverse handlers and performs the `initialize` handshake.
   * On a bare Linux without libicu the self-contained host FailFasts at boot (§23);
   * that one crash is retried once with .NET's invariant-globalization fallback —
   * degraded collation, but the resx localization survives (fr measured end-to-end) —
   * rather than greeting a fresh install with a dead extension.
   */
  async start(): Promise<InitializeResult> {
    try {
      return await this.startOnce();
    } catch (err) {
      if (process.platform !== 'win32') {
        // Let stdio flush before reading the diagnosis: the FailFast text can arrive
        // on stderr after the RPC pipe already broke. 2 s guard for the spawn-error
        // path, where 'close' never fires.
        await Promise.race([this.closed, new Promise((r) => setTimeout(r, 2000))]);
        if (this.icuCrash) {
          this.options.log?.(
            '[host] libicu missing — retrying with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ' +
              '(degraded globalization; install libicu for full locale support)',
          );
          return this.startOnce({
            DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
            // Invariant mode alone refuses `new CultureInfo("fr")`: allow the named
            // (data-less) cultures so the satellite-resource lookup keeps working.
            DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY: '0',
          });
        }
      }
      throw err;
    }
  }

  private async startOnce(extraEnv?: Record<string, string>): Promise<InitializeResult> {
    if (this.conn) {
      throw new Error('Host already running');
    }
    this.stopping = false;
    this.starting = true;
    this.icuCrash = false;
    this.spawnError = undefined;

    const proc = cp.spawn(this.options.hostPath, [], {
      cwd: this.options.rootDir,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      env: extraEnv ? { ...process.env, ...extraEnv } : undefined,
    });
    this.proc = proc;
    this.closed = new Promise((resolve) => proc.once('close', () => resolve()));

    proc.stderr?.setEncoding('utf8');
    proc.stderr?.on('data', (chunk: string) => {
      if (chunk.includes("Couldn't find a valid ICU package")) {
        this.icuCrash = true;
      }
      for (const line of chunk.split(/\r?\n/)) {
        if (line.trim().length > 0) {
          this.options.log?.(`[host] ${line}`);
        }
      }
    });

    proc.on('exit', (code) => {
      const crashed = !this.stopping;
      const starting = this.starting;
      this.teardown();
      if (crashed) {
        this.options.log?.(`[host] exited unexpectedly (code ${code})`);
        // During the handshake the failure surfaces through start() rejecting (or its
        // libicu retry succeeding); the crash popup is for an established session.
        if (!starting) {
          this.options.onCrash?.(code);
        }
      }
    });

    // ⚠ A spawn failure (EACCES on a host without its exec bit, ENOENT) emits 'error' and never
    // 'exit': unlistened, it was thrown into the extension host as an uncaught exception, and the
    // handshake below waited on a pipe nobody would ever answer.
    proc.on('error', (err) => {
      this.spawnError = err;
      this.options.log?.(`[host] could not run ${this.options.hostPath}: ${err.message}`);
      this.teardown();
    });

    const conn = rpc.createMessageConnection(
      new rpc.StreamMessageReader(proc.stdout!),
      new rpc.StreamMessageWriter(proc.stdin!),
    );
    this.conn = conn;

    // ── Reverse requests (host → editor) ────────────────────────────────────
    // The cancellation token carries the host's $/cancelRequest: a cancelled agent turn must
    // retire its approval card instead of leaving a ghost the user can still click (§27.5).
    conn.onRequest('approval/request', (note: ApprovalNote, token: CancellationToken) =>
      this.delegate.approvalRequest(note, token));
    conn.onRequest('editor/activeDocument', () => this.delegate.activeDocument());
    conn.onRequest('editor/insertAtCursor', (p: { text: string }) => this.delegate.insertAtCursor(p.text));
    conn.onRequest('editor/replaceSelection', (p: { text: string }) => this.delegate.replaceSelection(p.text));
    conn.onRequest('editor/diagnostics', () => this.delegate.editorDiagnostics());
    // Non-Windows hosts have no DPAPI: MCP OAuth tokens are encrypted by the editor's own
    // secret store (OS keychain) instead of being written in clear text.
    conn.onRequest('secrets/protect', (p: { key: string; value: string }) =>
      this.delegate.protectSecret(p.key, p.value));
    conn.onRequest('secrets/unprotect', (p: { key: string; value: string }) =>
      this.delegate.unprotectSecret(p.key, p.value));

    // ── Reverse debugger requests (roadmap §21) ─────────────────────────────
    const debug = this.debugDelegate;
    if (debug) {
      conn.onRequest('debug/addBreakpoint', (p: DebugBreakpointParams) => debug.addBreakpoint(p.file, p.line));
      conn.onRequest('debug/removeBreakpoint', (p: DebugBreakpointParams) => debug.removeBreakpoint(p.file, p.line));
      conn.onRequest('debug/listBreakpoints', () => debug.listBreakpoints());
      conn.onRequest('debug/start', () => debug.start());
      conn.onRequest('debug/continue', () => debug.continue());
      conn.onRequest('debug/step', (p: DebugStepParams) => debug.step(p));
      conn.onRequest('debug/state', () => debug.state());
      conn.onRequest('debug/evaluate', (p: DebugEvaluateParams) => debug.evaluate(p));
      conn.onRequest('debug/stop', () => debug.stop());
      conn.onRequest('debug/captureTest', (p: DebugCaptureTestParams) => debug.captureTest(p));
    }

    // ── Streamed chat notifications ─────────────────────────────────────────
    conn.onNotification('chat/token', (n: TextNote) => this.events.onToken?.(n.text));
    conn.onNotification('chat/thinking', (n: ThinkingNote) => this.events.onThinking?.(n.text ?? null));
    conn.onNotification('chat/step', (n: TextNote) => this.events.onStep?.(n.text));
    conn.onNotification('chat/plan', (n: PlanNotice) => this.events.onPlan?.(n));
    conn.onNotification('chat/stepUpdate', (n: StepUpdateNotice) => this.events.onStepUpdate?.(n));
    conn.onNotification('chat/tool', (n: ToolNotice) => this.events.onTool?.(n));
    conn.onNotification('chat/streamReset', () => this.events.onStreamReset?.());
    conn.onNotification('chat/stepPaused', () => this.events.onStepPaused?.());
    conn.onNotification('chat/stepResumed', () => this.events.onStepResumed?.());
    // Dedicated channel: as a `chat/step` status line the notice was wiped by the next
    // setBusy(false) — a task finishing while the user looked away left no trace (revue §3.6).
    conn.onNotification('task/finished', (n: TextNote) => this.events.onTaskFinished?.(n.text));

    conn.onError((err) => this.options.log?.(`[rpc] error: ${String(err)}`));
    conn.listen();

    const params: InitializeParams = {
      rootDir: this.options.rootDir,
      locale: this.options.locale,
      clientName: this.options.clientName ?? 'vscode',
      debug: this.debugDelegate !== undefined,
    };
    try {
      this.info = await conn.sendRequest<InitializeResult>('initialize', params);
    } catch (err) {
      // A process that never ran kills the connection: its spawn error names the real cause.
      throw this.spawnError ?? err;
    }
    this.starting = false;
    this.options.log?.(
      `[host] initialized v${this.info.hostVersion} (provider ${this.info.provider}, model ${this.info.defaultModel})`,
    );
    return this.info;
  }

  /** Graceful shutdown; falls back to kill if the process lingers. */
  async stop(): Promise<void> {
    this.stopping = true;
    const conn = this.conn;
    const proc = this.proc;
    try {
      conn?.sendNotification('shutdown');
    } catch {
      // connection already dead — kill below
    }
    this.teardown();
    if (proc && proc.exitCode === null) {
      await new Promise<void>((resolve) => {
        const timer = setTimeout(() => {
          proc.kill();
          resolve();
        }, 3000);
        proc.once('exit', () => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
  }

  // ── Requests ───────────────────────────────────────────────────────────────

  chatSend(params: ChatSendParams): Promise<ChatSendResult> {
    return this.whileChatBusy(() => this.connection().sendRequest<ChatSendResult>('chat/send', params));
  }

  /** Fill-in-the-Middle completion. Cancelling `token` sends `$/cancelRequest`,
   * which the host maps to a CancellationToken that aborts the LLM call. */
  fimComplete(
    params: { prefix: string; suffix: string; maxTokens?: number; temperature?: number; model?: string },
    token?: CancellationToken,
  ): Promise<string> {
    return this.connection().sendRequest<string>('fim/complete', params, token);
  }

  /** Inferpal's inline-completion switch and the debounce of its mode, as saved in its settings. */
  fimSettings(): Promise<FimSettingsResult> {
    return this.connection().sendRequest<FimSettingsResult>('fim/settings');
  }

  chatCancel(): Promise<void> {
    return this.connection().sendRequest('chat/cancel');
  }

  chatReset(): Promise<void> {
    return this.connection().sendRequest('chat/reset');
  }

  /** Takes the last question and everything after it out of the host history (regenerate). */
  chatRollbackLastTurn(): Promise<boolean> {
    return this.connection().sendRequest<boolean>('chat/rollbackLastTurn');
  }

  /** Releases a step-mode pause (the agent proceeds to its next action). */
  chatResumeStep(): Promise<void> {
    return this.connection().sendRequest('chat/resumeStep');
  }

  /** Slash commands the host serves headlessly. `promptHistory` (most-recent-last)
   * feeds /phistory; long commands are cancellable via chatCancel(). Flagged chat-busy like
   * chat/send: /tdd, /bench and /arena are multi-minute inferences holding the GPU lease, and
   * FIM used to queue behind them on every keystroke (pre-1.6.0 architecture review — the cost on
   * instant slashes is a skipped FIM for a few milliseconds). */
  commandSlash(text: string, promptHistory?: string[]): Promise<SlashCommandResult> {
    return this.whileChatBusy(() =>
      this.connection().sendRequest<SlashCommandResult>('command/slash', { text, promptHistory }),
    );
  }

  /** Declarative settings form (tabs → sections → fields) served from the Core. */
  settingsSchema(): Promise<SettingsSchema> {
    return this.connection().sendRequest<SettingsSchema>('settings/schema');
  }

  /** Context X-Ray panel model (interactive /xray V2). */
  xrayPanel(): Promise<XRayPanel> {
    return this.connection().sendRequest<XRayPanel>('xray/panel');
  }

  /** Switches one prompt section on/off for the next turns; returns the refreshed panel. */
  xrayToggle(id: string, enabled: boolean): Promise<XRayPanel> {
    return this.connection().sendRequest<XRayPanel>('xray/toggle', { id, enabled });
  }

  /** In-place code action (fix / refactor / doc): the host runs the model step and returns
   * per-hunk offset edits; previewing and applying stay editor-side. Flagged as chat-busy
   * so FIM requests skip instead of queueing behind the rewrite on the shared GPU. */
  codeActionRun(params: CodeActionParams): Promise<CodeActionResult> {
    return this.whileChatBusy(() => this.connection().sendRequest<CodeActionResult>('codeAction/run', params));
  }

  /** Models of the backend the caller is LOOKING AT. Without overrides this is the saved
   *  configuration; the settings panel passes what its form currently holds, because a refresh that
   *  lists the previous URL's models is a control that answers about something else. */
  modelsList(overrides?: { baseUrl?: string; provider?: string; apiKey?: string }): Promise<string[]> {
    return this.connection().sendRequest<string[]>('models/list', overrides ?? {});
  }

  /** Renders the export document - the same one the Visual Studio window produces, because it is
   *  the same Core exporter. The adapter picks the format and supplies its bubbles. */
  chatExport(params: ChatExportParams): Promise<string> {
    return this.connection().sendRequest<string>('chat/export', params);
  }

  /** Probes `baseUrl` with `apiKey` — the values in the form, not the saved ones — and names the
   *  backend that answered. Omitting either falls back to the saved configuration. */
  connectionCheck(baseUrl?: string, apiKey?: string): Promise<ConnectionCheckResult> {
    return this.connection().sendRequest<ConnectionCheckResult>('connection/check', { baseUrl, apiKey });
  }

  /** Connection badge for the header (reachability + VRAM line). Polled — never throws
   * into the UI, the caller treats a rejection as "unreachable". */
  backendStatus(): Promise<BackendStatusResult> {
    return this.connection().sendRequest<BackendStatusResult>('backend/status');
  }

  /** Slash commands for the autocomplete popup (built-ins + user templates). */
  commandList(): Promise<SlashCommandInfo[]> {
    return this.connection().sendRequest<SlashCommandInfo[]>('command/list');
  }

  /** Typed @mention categories (localized by the host). */
  mentionCategories(): Promise<MentionCategory[]> {
    return this.connection().sendRequest<MentionCategory[]>('mention/categories');
  }

  /** @file / @folder fuzzy sub-search under the workspace root. */
  mentionSearch(category: string, query: string): Promise<MentionItem[]> {
    return this.connection().sendRequest<MentionItem[]>('mention/search', { category, query });
  }

  /** Materializes a mention host-side (@tree, @diff, @folder, @code). */
  mentionResolve(category: string, value?: string): Promise<MentionResolveResult> {
    return this.connection().sendRequest<MentionResolveResult>('mention/resolve', { category, value });
  }

  configGet(): Promise<string> {
    return this.connection().sendRequest<string>('config/get');
  }

  /** Localized labels/hints/sections of the settings UI — the same .resx strings as the
   * Visual Studio settings window (keys = resx resource names). */
  settingsStrings(): Promise<Record<string, string>> {
    return this.connection().sendRequest<Record<string, string>>('settings/strings');
  }

  /** Returns what the save could not use. The count is computed BY THE HOST: the permission DSL
   *  lives in the Core, and re-reading it here would be a second implementation of the same rule -
   *  hence a programmed divergence. */
  configUpdate(json: string, base?: string): Promise<ConfigUpdateResult> {
    return this.connection().sendRequest<ConfigUpdateResult>('config/update', { json, base });
  }

  /** The files pinned into every request. */
  pinsList(): Promise<{ pins: string[]; notice?: string | null }> {
    return this.connection().sendRequest('pins/list');
  }

  /** Pins a file (same cap and decision as the Visual Studio window); the system prompt follows. */
  pinsAdd(path: string): Promise<{ pins: string[]; notice?: string | null }> {
    return this.connection().sendRequest('pins/add', { path });
  }

  /** Takes a pinned file out of every request. */
  pinsRemove(path: string): Promise<{ pins: string[]; notice?: string | null }> {
    return this.connection().sendRequest('pins/remove', { path });
  }

  indexStart(): Promise<void> {
    return this.connection().sendRequest('index/start');
  }

  indexStatus(): Promise<IndexStatusResult> {
    return this.connection().sendRequest<IndexStatusResult>('index/status');
  }

  // ── Sessions (persisted host-side, same store as the VS extension) ─────────

  /** `archive`: the conversation being left — saved, but not the file the next one lives in. */
  sessionSave(name: string, messages: SavedMessage[], archive = false): Promise<void> {
    return this.connection().sendRequest('session/save', { name, messages, archive });
  }

  sessionList(): Promise<SessionSummary[]> {
    return this.connection().sendRequest<SessionSummary[]>('session/list');
  }

  /** Rebuilds the host history from the saved session and returns the transcript. */
  sessionLoad(name: string): Promise<SessionLoadResult | null> {
    return this.connection().sendRequest<SessionLoadResult | null>('session/load', { name });
  }

  sessionDelete(name: string): Promise<boolean> {
    return this.connection().sendRequest<boolean>('session/delete', { name });
  }

  /**
   * Forks the conversation at `turn` (`/branch <n>`): the host writes the branch (and the parent
   * when it had no file yet), rebuilds its history from the truncated transcript and returns it.
   * Null when the turn doesn't exist.
   */
  sessionBranch(turn: number, messages: SavedMessage[]): Promise<SessionBranchResult | null> {
    return this.connection().sendRequest<SessionBranchResult | null>('session/branch', { turn, messages });
  }

  /**
   * `/branch [args]` decided on `messages` — the displayed transcript the fork runs on, not the host's
   * history: a message to show, a turn to fork at, or a session to switch to.
   */
  sessionBranchCommand(args: string, messages: SavedMessage[]): Promise<SessionBranchCommandResult> {
    return this.connection().sendRequest<SessionBranchCommandResult>('session/branchCommand', { args, messages });
  }

  /**
   * LLM-generated name for the conversation (utility model, Model Router). `text` empty means
   * "the first user message of the host session". Never rejects for a backend problem — the host
   * falls back to a snippet of the message.
   */
  sessionTitle(text?: string): Promise<SessionTitleResult> {
    return this.connection().sendRequest<SessionTitleResult>('session/title', { text: text ?? '' });
  }

  // ── Notifications (fire-and-forget document sync) ──────────────────────────

  didOpen(doc: DocumentParams): void {
    this.conn?.sendNotification('textDocument/didOpen', doc);
  }

  didChange(doc: DocumentParams): void {
    this.conn?.sendNotification('textDocument/didChange', doc);
  }

  didClose(path: string): void {
    this.conn?.sendNotification('textDocument/didClose', { path });
  }

  didChangeActiveDocument(path: string | null): void {
    this.conn?.sendNotification('editor/didChangeActiveDocument', { path: path ?? '' });
  }

  // ── Internals ──────────────────────────────────────────────────────────────

  private connection(): rpc.MessageConnection {
    if (!this.conn) {
      throw new Error('Inferpal host is not running');
    }
    return this.conn;
  }

  private teardown(): void {
    try {
      this.conn?.dispose();
    } catch {
      // already disposed
    }
    this.conn = undefined;
    this.proc = undefined;
    this.info = undefined;
  }
}
