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
  DebugEvaluateParams,
  DebugStepParams,
  BackendStatusResult,
  ChatSendParams,
  ChatSendResult,
  CodeActionParams,
  CodeActionResult,
  DocumentParams,
  EditResultDto,
  IndexStatusResult,
  InitializeParams,
  InitializeResult,
  MentionCategory,
  MentionItem,
  MentionResolveResult,
  PlanNotice,
  SavedMessage,
  SessionBranchResult,
  SettingsSchema,
  SessionLoadResult,
  SessionTitleResult,
  SessionSummary,
  SlashCommandInfo,
  SlashCommandResult,
  StepUpdateNotice,
  TextNote,
  ToolNotice,
  XRayPanel,
} from './protocol';

/** Reverse-RPC surface the editor side must provide before `start()`. */
export interface EditorDelegate {
  approvalRequest(note: ApprovalNote): Promise<number>;
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
  onThinking?(text: string): void;
  onStep?(text: string): void;
  onPlan?(plan: PlanNotice): void;
  onStepUpdate?(update: StepUpdateNotice): void;
  onTool?(tool: ToolNotice): void;
  onStreamReset?(): void;
  onStepPaused?(): void;
  onStepResumed?(): void;
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
  /** Resolves when the current process closed (stdio flushed) — the ICU message can
   * land on stderr after the RPC pipe already broke, so `start()` waits on this
   * before deciding whether the crash was the libicu case. */
  private closed: Promise<void> | undefined;

  /** Host handshake answer, available after start(). */
  public info: InitializeResult | undefined;

  /** True while a chat turn is in flight. FIM must skip then: the agent run holds
   * the host-side GPU lease for its whole duration and the request would just queue. */
  public isChatBusy = false;

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

    const conn = rpc.createMessageConnection(
      new rpc.StreamMessageReader(proc.stdout!),
      new rpc.StreamMessageWriter(proc.stdin!),
    );
    this.conn = conn;

    // ── Reverse requests (host → editor) ────────────────────────────────────
    conn.onRequest('approval/request', (note: ApprovalNote) => this.delegate.approvalRequest(note));
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
    }

    // ── Streamed chat notifications ─────────────────────────────────────────
    conn.onNotification('chat/token', (n: TextNote) => this.events.onToken?.(n.text));
    conn.onNotification('chat/thinking', (n: TextNote) => this.events.onThinking?.(n.text));
    conn.onNotification('chat/step', (n: TextNote) => this.events.onStep?.(n.text));
    conn.onNotification('chat/plan', (n: PlanNotice) => this.events.onPlan?.(n));
    conn.onNotification('chat/stepUpdate', (n: StepUpdateNotice) => this.events.onStepUpdate?.(n));
    conn.onNotification('chat/tool', (n: ToolNotice) => this.events.onTool?.(n));
    conn.onNotification('chat/streamReset', () => this.events.onStreamReset?.());
    conn.onNotification('chat/stepPaused', () => this.events.onStepPaused?.());
    conn.onNotification('chat/stepResumed', () => this.events.onStepResumed?.());

    conn.onError((err) => this.options.log?.(`[rpc] error: ${String(err)}`));
    conn.listen();

    const params: InitializeParams = {
      rootDir: this.options.rootDir,
      locale: this.options.locale,
      clientName: this.options.clientName ?? 'vscode',
      debug: this.debugDelegate !== undefined,
    };
    this.info = await conn.sendRequest<InitializeResult>('initialize', params);
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

  async chatSend(params: ChatSendParams): Promise<ChatSendResult> {
    this.isChatBusy = true;
    try {
      return await this.connection().sendRequest<ChatSendResult>('chat/send', params);
    } finally {
      this.isChatBusy = false;
    }
  }

  /** Fill-in-the-Middle completion. Cancelling `token` sends `$/cancelRequest`,
   * which the host maps to a CancellationToken that aborts the LLM call. */
  fimComplete(
    params: { prefix: string; suffix: string; maxTokens?: number; temperature?: number; model?: string },
    token?: CancellationToken,
  ): Promise<string> {
    return this.connection().sendRequest<string>('fim/complete', params, token);
  }

  chatCancel(): Promise<void> {
    return this.connection().sendRequest('chat/cancel');
  }

  chatReset(): Promise<void> {
    return this.connection().sendRequest('chat/reset');
  }

  /** Releases a step-mode pause (the agent proceeds to its next action). */
  chatResumeStep(): Promise<void> {
    return this.connection().sendRequest('chat/resumeStep');
  }

  /** Slash commands the host serves headlessly. `promptHistory` (most-recent-last)
   * feeds /phistory; long commands are cancellable via chatCancel(). */
  commandSlash(text: string, promptHistory?: string[]): Promise<SlashCommandResult> {
    return this.connection().sendRequest<SlashCommandResult>('command/slash', { text, promptHistory });
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
  async codeActionRun(params: CodeActionParams): Promise<CodeActionResult> {
    this.isChatBusy = true;
    try {
      return await this.connection().sendRequest<CodeActionResult>('codeAction/run', params);
    } finally {
      this.isChatBusy = false;
    }
  }

  modelsList(): Promise<string[]> {
    return this.connection().sendRequest<string[]>('models/list');
  }

  connectionCheck(): Promise<boolean> {
    return this.connection().sendRequest<boolean>('connection/check');
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

  configUpdate(json: string): Promise<void> {
    return this.connection().sendRequest('config/update', { json });
  }

  indexStart(): Promise<void> {
    return this.connection().sendRequest('index/start');
  }

  indexStatus(): Promise<IndexStatusResult> {
    return this.connection().sendRequest<IndexStatusResult>('index/status');
  }

  // ── Sessions (persisted host-side, same store as the VS extension) ─────────

  sessionSave(name: string, messages: SavedMessage[]): Promise<void> {
    return this.connection().sendRequest('session/save', { name, messages });
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
