// Spawns and supervises the Inferpal.Host process and exposes its JSON-RPC surface
// as a typed API. Deliberately free of any 'vscode' import so it can be smoke-tested
// with plain Node against the real host binary.
import * as cp from 'child_process';
import * as rpc from 'vscode-jsonrpc/node';
import type { CancellationToken } from 'vscode-jsonrpc';
import {
  ActiveDocumentDto,
  ApprovalNote,
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
  SessionLoadResult,
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

  /** Host handshake answer, available after start(). */
  public info: InitializeResult | undefined;

  /** True while a chat turn is in flight. FIM must skip then: the agent run holds
   * the host-side GPU lease for its whole duration and the request would just queue. */
  public isChatBusy = false;

  constructor(
    private readonly options: HostClientOptions,
    private readonly delegate: EditorDelegate,
  ) {}

  get isRunning(): boolean {
    return this.conn !== undefined;
  }

  /** Replaces the streamed-chat event sinks (the chat view re-registers on resolve). */
  setChatEvents(events: ChatEvents): void {
    this.events = events;
  }

  /** Spawns the host, wires reverse handlers and performs the `initialize` handshake. */
  async start(): Promise<InitializeResult> {
    if (this.conn) {
      throw new Error('Host already running');
    }
    this.stopping = false;

    const proc = cp.spawn(this.options.hostPath, [], {
      cwd: this.options.rootDir,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    this.proc = proc;

    proc.stderr?.setEncoding('utf8');
    proc.stderr?.on('data', (chunk: string) => {
      for (const line of chunk.split(/\r?\n/)) {
        if (line.trim().length > 0) {
          this.options.log?.(`[host] ${line}`);
        }
      }
    });

    proc.on('exit', (code) => {
      const crashed = !this.stopping;
      this.teardown();
      if (crashed) {
        this.options.log?.(`[host] exited unexpectedly (code ${code})`);
        this.options.onCrash?.(code);
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

    // ── Streamed chat notifications ─────────────────────────────────────────
    conn.onNotification('chat/token', (n: TextNote) => this.events.onToken?.(n.text));
    conn.onNotification('chat/thinking', (n: TextNote) => this.events.onThinking?.(n.text));
    conn.onNotification('chat/step', (n: TextNote) => this.events.onStep?.(n.text));
    conn.onNotification('chat/plan', (n: PlanNotice) => this.events.onPlan?.(n));
    conn.onNotification('chat/stepUpdate', (n: StepUpdateNotice) => this.events.onStepUpdate?.(n));
    conn.onNotification('chat/tool', (n: ToolNotice) => this.events.onTool?.(n));
    conn.onNotification('chat/streamReset', () => this.events.onStreamReset?.());

    conn.onError((err) => this.options.log?.(`[rpc] error: ${String(err)}`));
    conn.listen();

    const params: InitializeParams = {
      rootDir: this.options.rootDir,
      locale: this.options.locale,
      clientName: this.options.clientName ?? 'vscode',
    };
    this.info = await conn.sendRequest<InitializeResult>('initialize', params);
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

  /** Slash commands the host serves headlessly. `promptHistory` (most-recent-last)
   * feeds /phistory; long commands are cancellable via chatCancel(). */
  commandSlash(text: string, promptHistory?: string[]): Promise<SlashCommandResult> {
    return this.connection().sendRequest<SlashCommandResult>('command/slash', { text, promptHistory });
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
