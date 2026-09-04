// Sidebar chat: WebviewViewProvider + typed postMessage protocol. The extension host
// (this class) is the source of truth for the transcript — the webview can be destroyed
// on hide and is re-hydrated on every resolveWebviewView.
import * as vscode from 'vscode';
import type { CancellationToken } from 'vscode-jsonrpc';
import { HostClient } from './hostClient';
import { hostUnavailableMessage, promptOpenFolder } from './hostStatus';
import { renderChatHtml } from './webview/chatWebviewHtml';
import { pickSession, renderExport, toSavedMessages, toTranscript } from './chatSessions';
import { CodeActionResult, SavedMessage, SlashEffect } from './protocol';
import {
  WebviewToExt,
  WvBackendStatus,
  WvMentionCategory,
  WvPlan,
  WvSlashCommand,
  WvTranscriptItem,
} from './webviewMessages';

const HISTORY_KEY = 'inferpal.promptHistory';
const HISTORY_MAX = 50;
const STATUS_POLL_MS = 30_000;

export class ChatViewProvider implements vscode.WebviewViewProvider {
  public static readonly viewId = 'inferpal.chat';

  private view: vscode.WebviewView | undefined;
  private readonly transcript: WvTranscriptItem[] = [];
  private models: string[] = [];
  private model = '';
  private busy = false;
  private streamText = '';
  private approvalSeq = 0;
  private readonly pendingApprovals = new Map<number, (answer: number) => void>();

  // ── VS-parity state pushed to the webview ───────────────────────────────────
  private status: WvBackendStatus | null = null;
  private commands: WvSlashCommand[] = [];
  private plan: WvPlan | null = null;
  private contextWindow = 0;
  private toolBubblesExpanded = false;
  private promptTokens = 0;
  private lastTokens = 0;
  private statusTimer: NodeJS.Timeout | undefined;
  /** Context chips (slash attachChip effects, @-mentions, "+" menu), consumed by the next turn. */
  private pendingAttachments: { name: string; content: string }[] = [];
  private mentionCats: WvMentionCategory[] = [];

  constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly getHost: () => HostClient | undefined,
    private readonly getActiveEditor: () => vscode.TextEditor | undefined,
    private readonly getDiagnostics: () => Promise<string | null>,
    private readonly log: (line: string) => void,
  ) {}

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
    view.webview.onDidReceiveMessage((msg: WebviewToExt) => this.onMessage(msg));
    view.onDidDispose(() => {
      if (this.view === view) {
        this.view = undefined;
      }
      // A card the user can no longer answer must fail closed.
      this.denyAllPending();
    });
  }

  /**
   * Shows an approval card in the chat and resolves with the user's answer
   * (0 deny / 1 once / 2 always). Returns undefined when the card cannot be
   * shown (no resolved view) — the caller then falls back to a modal dialog.
   */
  async requestApproval(message: string, token?: CancellationToken): Promise<number | undefined> {
    if (!this.view) {
      return undefined;
    }
    // Surface the card: reveal the view if it is hidden (agent runs can outlive focus).
    if (!this.view.visible) {
      try {
        await vscode.commands.executeCommand('inferpal.chat.focus');
      } catch {
        // focus command unavailable — the card still lands in the retained webview
      }
      if (!this.view) {
        return undefined; // disposed while revealing
      }
    }
    const id = ++this.approvalSeq;
    const answer = new Promise<number>((resolve) => this.pendingApprovals.set(id, resolve));
    // §27.5 — turn cancelled while the card is up: deny, and retire the card in the webview so
    // it cannot be answered into a run that no longer exists (ghost card).
    const cancelSub = token?.onCancellationRequested(() => this.dismissApproval(id));
    this.post({ type: 'approval', id, message });
    try {
      return await answer;
    } finally {
      cancelSub?.dispose();
    }
  }

  private dismissApproval(id: number): void {
    const resolve = this.pendingApprovals.get(id);
    if (!resolve) {
      return; // already answered
    }
    this.pendingApprovals.delete(id);
    this.post({ type: 'approvalDismiss', id });
    resolve(0);
  }

  /** New conversation: clears both the host history and the local transcript. */
  async resetConversation(): Promise<void> {
    const host = this.getHost();
    if (host?.isRunning) {
      try {
        await host.chatReset();
      } catch (err) {
        // The host refused (a turn is in flight): clearing the webview anyway used to desync the
        // two — an empty transcript over a full host history, and the running turn's answer then
        // landed alone in a "new" conversation carrying the old context (pre-1.6.0 architecture review, §2.7).
        this.log(`[chat] reset refused: ${String(err)}`);
        void vscode.window.showWarningMessage(
          vscode.l10n.t('A turn is still running — stop it before starting a new conversation.'));
        return;
      }
    }
    // Archive after the host accepted the reset — fire-and-forget so the UI clears
    // immediately while the utility model names the session in the background.
    this.archiveConversation();
    this.transcript.length = 0;
    this.streamText = '';
    this.plan = null;
    this.promptTokens = 0;
    this.lastTokens = 0;
    this.hydrate();
  }

  /** Called by the activator once the host handshake finished. */
  async onHostReady(): Promise<void> {
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
      onThinking: () => this.post({ type: 'thinking' }),
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
      onTool: (tool) => {
        const item: WvTranscriptItem = {
          role: 'tool',
          text: tool.name,
          toolInput: tool.input,
          toolOutput: tool.output,
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
          timestamp: item.timestamp!,
          expanded: this.toolBubblesExpanded,
        });
      },
      onStreamReset: () => {
        this.streamText = '';
        this.post({ type: 'streamReset' });
      },
      onStepPaused: () => this.post({ type: 'stepPaused' }),
      onStepResumed: () => this.post({ type: 'stepResumed' }),
      onTaskFinished: (text) => {
        // A persistent assistant bubble, like the VS front-end — not an ephemeral status line
        // wiped by the next setBusy (pre-1.6.0 architecture review, §3.6).
        const item: WvTranscriptItem = { role: 'assistant', text, timestamp: ChatViewProvider.now() };
        this.append(item);
        this.post({ type: 'assistant', text, timestamp: item.timestamp! });
      },
    });

    this.model = vscode.workspace.getConfiguration('inferpal').get<string>('model', '') || host.info?.defaultModel || '';
    try {
      this.models = await host.modelsList();
    } catch (err) {
      // ⚠ This is NOT the unreachable-backend path, contrary to what this block long implied:
      // measured 2026-09-03, all three providers swallow the HTTP failure and return an EMPTY
      // list, so `models/list` SUCCEEDS. This catch only ever sees a host or RPC failure. The
      // empty list goes through the success path instead — and the view is what names it now
      // (a disabled "no model listed" option).
      this.log(`[chat] models/list failed: ${String(err)}`);
      this.models = this.model ? [this.model] : [];
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
      const cfg = JSON.parse(await host.configGet()) as { contextWindowSize?: number; toolBubblesExpanded?: boolean };
      this.contextWindow = cfg.contextWindowSize ?? 0;
      this.toolBubblesExpanded = cfg.toolBubblesExpanded === true;
    } catch (err) {
      this.log(`[chat] config/get failed: ${String(err)}`);
    }

    this.startStatusPolling();
    await this.pollBackendStatus();

    // Continuity across restarts: bring back the auto-saved conversation, like the VS VM.
    if (this.transcript.length === 0) {
      try {
        const last = await host.sessionLoad('last_session');
        if (last && last.messages.length > 0) {
          this.applySession(last.messages);
          return; // applySession hydrates
        }
      } catch (err) {
        this.log(`[chat] last-session restore failed: ${String(err)}`);
      }
    }
    this.hydrate();
  }

  /** True when there is something in the transcript (settings panel warns before reset). */
  hasConversation(): boolean {
    return this.transcript.length > 0;
  }

  /** Called after the settings panel saved: re-reads the UI-relevant config bits. */
  async configSaved(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      const cfg = JSON.parse(await host.configGet()) as { contextWindowSize?: number; toolBubblesExpanded?: boolean };
      this.contextWindow = cfg.contextWindowSize ?? 0;
      this.toolBubblesExpanded = cfg.toolBubblesExpanded === true;
    } catch (err) {
      this.log(`[chat] config/get failed: ${String(err)}`);
    }
    try {
      this.commands = await host.commandList();
    } catch {
      // keep the previous autocomplete list
    }
    try {
      const listed = await host.modelsList();
      // ⚠ An empty list used to REPLACE the previous one here, while the catch below promised the
      // opposite — and it is the success path that returns it, not the catch (see bootstrap). So
      // saving the settings with the backend down silently emptied the picker. The previous list is
      // kept only when the new one is empty: a backend that REALLY lost models must still be able
      // to remove them.
      if (listed.length > 0) {
        this.models = listed;
      }
    } catch {
      // host or RPC failure: keep the previous list (see bootstrap for why this is not the
      // "backend unreachable" case).
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

  private async pollBackendStatus(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      this.status = null;
      this.post({ type: 'backendStatus', status: { connected: false, vramBadge: '' } });
      return;
    }
    try {
      const s = await host.backendStatus();
      this.status = { connected: s.connected, vramBadge: s.vramBadge };
    } catch {
      this.status = { connected: false, vramBadge: '' };
    }
    this.post({ type: 'backendStatus', status: this.status });
  }

  // ── Sessions ────────────────────────────────────────────────────────────────

  /** Persistable view of the transcript (same shape the VS extension saves). */
  private snapshot(): SavedMessage[] {
    return toSavedMessages(this.transcript);
  }

  /** Replaces the transcript with a restored session (host history already rebuilt). */
  private applySession(messages: SavedMessage[]): void {
    this.transcript.length = 0;
    this.transcript.push(...toTranscript(messages));
    this.trimTranscript();
    this.streamText = '';
    this.plan = null;
    this.hydrate();
  }

  private autoSaveLast(): void {
    const host = this.getHost();
    if (!host?.isRunning || this.transcript.length === 0) {
      return;
    }
    host.sessionSave('last_session', this.snapshot()).catch((err) => {
      this.log(`[chat] auto-save failed: ${String(err)}`);
    });
  }

  /**
   * `/branch <n>`: forks the conversation host-side and continues in the branch. The transcript
   * sent over is the display one (tool names and timestamps only exist here), minus the trailing
   * `/branch …` bubble — the command itself is not part of the conversation being forked.
   * Returns the confirmation bubble, or null when the fork was refused.
   */
  private async branchAtTurn(turn: number): Promise<string | null> {
    const host = this.getHost();
    if (!host?.isRunning || !Number.isFinite(turn)) {
      return null;
    }
    const messages = this.snapshot();
    const last = messages[messages.length - 1];
    if (last?.role === 'user' && last.content.startsWith('/')) {
      messages.pop();
    }
    try {
      const branch = await host.sessionBranch(turn, messages);
      if (!branch) {
        return null;
      }
      this.applySession(branch.messages);
      return branch.message;
    } catch (err) {
      this.log(`[chat] branch failed: ${String(err)}`);
      return null;
    }
  }

  /** `/branch <name>`: switching to a branch is a plain session load. */
  private async switchToSession(name: string): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      this.append({ role: 'error', text: hostUnavailableMessage(), timestamp: ChatViewProvider.now() });
      this.hydrate();
      return;
    }
    if (!name) {
      return;
    }
    try {
      const loaded = await host.sessionLoad(name);
      if (loaded) {
        this.applySession(loaded.messages);
      }
    } catch (err) {
      this.log(`[chat] branch switch failed: ${String(err)}`);
    }
  }

  /**
   * Archives the conversation being discarded under an LLM-generated name (`session/title`),
   * the way the VS front-end does on `/clear`. Snapshot and first message are captured
   * synchronously — the caller clears the transcript right after.
   */
  private archiveConversation(): void {
    const host = this.getHost();
    if (!host?.isRunning || this.transcript.length === 0) {
      return;
    }
    const messages = this.snapshot();
    const first = this.transcript.find((m) => m.role === 'user')?.text ?? '';
    void (async () => {
      try {
        const { fileName } = await host.sessionTitle(first);
        await host.sessionSave(fileName, messages);
      } catch (err) {
        this.log(`[chat] archive failed: ${String(err)}`);
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
    const first = this.transcript.find((m) => m.role === 'user')?.text ?? '';
    const suggested = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Window, title: vscode.l10n.t('Naming the session…') },
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
      prompt: vscode.l10n.t('Session name'),
      value: suggested.replace(/[\\/:*?"<>|\n]+/g, ' ').trim(),
    });
    if (!name) {
      return;
    }
    // §27.6 — 'last_session' is the auto-save slot (overwritten every turn): a user save under
    // that name would silently vanish. Same pattern as /plan's reserved set: suffix it.
    const safeName =
      name.trim().toLowerCase() === 'last_session' ? `${name.trim()}-session` : name.trim();
    await host.sessionSave(safeName, this.snapshot());
    void vscode.window.showInformationMessage(vscode.l10n.t('Session saved: {0}', safeName));
  }

  /** Command: pick a saved session and restore it (host history + transcript). */
  async loadSessionCommand(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      void vscode.window.showWarningMessage(hostUnavailableMessage());
      promptOpenFolder();
      return;
    }
    const pick = await this.pickSession(vscode.l10n.t('Pick a session to load'));
    if (!pick) {
      return;
    }
    const loaded = await host.sessionLoad(pick);
    if (loaded) {
      this.applySession(loaded.messages);
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
    const pick = await this.pickSession(vscode.l10n.t('Pick a session to delete'));
    if (pick) {
      await host.sessionDelete(pick);
    }
  }

  private pickSession(placeholder: string): Promise<string | undefined> {
    return pickSession(this.getHost()!, placeholder);
  }

  /** Command: export the conversation to a Markdown or text file (VS toolbar parity). */
  async exportCommand(): Promise<void> {
    if (this.transcript.length === 0) {
      void vscode.window.showInformationMessage(vscode.l10n.t('Nothing to export — the conversation is empty.'));
      return;
    }
    const target = await vscode.window.showSaveDialog({
      filters: { Markdown: ['md'], Text: ['txt'] },
      defaultUri: vscode.Uri.file('inferpal-chat.md'),
    });
    if (!target) {
      return;
    }
    await vscode.workspace.fs.writeFile(target, Buffer.from(renderExport(this.transcript), 'utf8'));
    void vscode.window.showInformationMessage(vscode.l10n.t('Conversation exported: {0}', target.fsPath));
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
        return;
      case 'ready':
        this.log('[chat] webview ready');
        this.hydrate();
        return;
      case 'send':
        await this.send(msg.text);
        return;
      case 'cancel':
        try {
          await this.getHost()?.chatCancel();
        } catch (err) {
          this.log(`[chat] cancel failed: ${String(err)}`);
        }
        return;
      case 'reset':
        await this.resetConversation();
        return;
      case 'pickModel': {
        this.model = msg.model;
        await vscode.workspace.getConfiguration('inferpal').update('model', msg.model, vscode.ConfigurationTarget.Workspace);
        // Also push the pick into the host's shared config: without it, `/model` (no argument)
        // kept answering the OLD model and every Model Router role that falls back to
        // DefaultModel (session titles, code actions) silently stayed on it (revue §3.5).
        // Read-modify-write of the full JSON — config/update replaces the whole object.
        const host = this.getHost();
        if (host?.isRunning) {
          try {
            const cfg = JSON.parse(await host.configGet()) as { defaultModel?: string };
            if (cfg.defaultModel !== msg.model) {
              cfg.defaultModel = msg.model;
              await host.configUpdate(JSON.stringify(cfg));
            }
          } catch (err) {
            this.log(`[chat] model pick → host config failed: ${String(err)}`);
          }
        }
        return;
      }
      case 'approvalAnswer': {
        const resolve = this.pendingApprovals.get(msg.id);
        this.pendingApprovals.delete(msg.id);
        resolve?.(msg.answer);
        return;
      }
      case 'mentionQuery': {
        const items = await this.mentionSuggestions(msg.query);
        this.post({ type: 'mentionSuggestions', items });
        return;
      }
      case 'openApprovalDiff': {
        const doc = await vscode.workspace.openTextDocument({ language: 'diff', content: msg.text });
        await vscode.window.showTextDocument(doc, { preview: true });
        return;
      }
      case 'xrayToggle': {
        // Applies the toggle host-side (next turns) and re-renders the refreshed panel.
        const host = this.getHost();
        if (!host?.isRunning) {
          return;
        }
        try {
          const panel = await host.xrayToggle(msg.id, msg.enabled);
          this.post({ type: 'xrayPanel', panel });
        } catch (err) {
          this.log(`[chat] xray/toggle failed: ${String(err)}`);
        }
        return;
      }
      case 'copyText':
        await vscode.env.clipboard.writeText(msg.text);
        return;
      case 'regenerate': {
        // v1: re-send the last user prompt as a fresh turn.
        const lastUser = [...this.transcript].reverse().find((m) => m.role === 'user');
        if (lastUser && !this.busy) {
          await this.send(lastUser.text);
        }
        return;
      }
      case 'toggleAgentMode': {
        const config = vscode.workspace.getConfiguration('inferpal');
        const enabled = !config.get<boolean>('agentMode', true);
        await config.update('agentMode', enabled, vscode.ConfigurationTarget.Workspace);
        this.post({ type: 'agentMode', enabled });
        return;
      }
      case 'retryConnection':
        await this.pollBackendStatus();
        return;
      case 'openXray':
        await this.openXray();
        return;
      case 'mentionSearch': {
        const host = this.getHost();
        if (!host?.isRunning) {
          return;
        }
        try {
          const items = await host.mentionSearch(msg.category, msg.query);
          this.post({ type: 'mentionResults', category: msg.category, items });
        } catch (err) {
          this.log(`[chat] mention/search failed: ${String(err)}`);
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
          this.log(`[chat] resumeStep failed: ${String(err)}`);
        }
        return;
      case 'removeChip':
        if (msg.index >= 0 && msg.index < this.pendingAttachments.length) {
          this.pendingAttachments.splice(msg.index, 1);
          this.postChips();
        }
        return;
      case 'attachActive': {
        const editor = this.getActiveEditor();
        if (!editor || editor.document.uri.scheme !== 'file') {
          void vscode.window.showInformationMessage(vscode.l10n.t('No file is open in the editor.'));
          return;
        }
        this.addChip('📄 ' + vscode.workspace.asRelativePath(editor.document.uri, false), editor.document.getText());
        return;
      }
      case 'attachSelection': {
        const editor = this.getActiveEditor();
        const text = editor && !editor.selection.isEmpty ? editor.document.getText(editor.selection) : '';
        if (!editor || text.length === 0) {
          void vscode.window.showInformationMessage(vscode.l10n.t('The selection is empty.'));
          return;
        }
        this.addChip('✂ ' + vscode.workspace.asRelativePath(editor.document.uri, false), text);
        return;
      }
      case 'attachBrowse': {
        const picked = await vscode.window.showOpenDialog({ canSelectMany: false });
        if (!picked || picked.length === 0) {
          return;
        }
        try {
          const doc = await vscode.workspace.openTextDocument(picked[0]);
          this.addChip('📄 ' + (picked[0].path.split('/').pop() ?? picked[0].fsPath), doc.getText());
        } catch (err) {
          this.log(`[chat] attachBrowse failed: ${String(err)}`);
        }
        return;
      }
    }
  }

  // ── Typed mentions: chip resolution ─────────────────────────────────────────

  private addChip(name: string, content: string): void {
    const MAX_CHARS = 60_000;
    this.pendingAttachments.push({
      name,
      content: content.length > MAX_CHARS ? content.slice(0, MAX_CHARS) + '\n…(truncated)' : content,
    });
    this.postChips();
  }

  private postChips(): void {
    this.post({ type: 'chips', chips: this.pendingAttachments.map((a) => ({ name: a.name })) });
  }

  /** Materializes an instant/selected mention: clipboard/problems/debugger are editor-side,
   * tree/diff/folder/code go through the host (mention/resolve). */
  private async resolveMention(category: string, value?: string): Promise<void> {
    switch (category) {
      case 'clipboard': {
        const text = await vscode.env.clipboard.readText();
        if (text.trim().length > 0) {
          this.addChip('📋 clipboard', text);
        }
        return;
      }
      case 'problems': {
        const report = await this.getDiagnostics();
        if (report && report.trim().length > 0) {
          this.addChip('⚠ problems', report);
        } else {
          void vscode.window.showInformationMessage(vscode.l10n.t('No problems in the Problems panel.'));
        }
        return;
      }
      case 'debugger': {
        const session = vscode.debug.activeDebugSession;
        if (!session) {
          void vscode.window.showInformationMessage(vscode.l10n.t('No debugger session is active.'));
          return;
        }
        this.addChip('🐞 ' + session.name, `Debug session: ${session.name} (type: ${session.type})`);
        return;
      }
      default: {
        const host = this.getHost();
        if (!host?.isRunning) {
          return;
        }
        try {
          const result = await host.mentionResolve(category, value);
          if (result.name && result.content) {
            this.addChip(result.name, result.content);
          }
        } catch (err) {
          this.log(`[chat] mention/resolve failed: ${String(err)}`);
        }
        return;
      }
    }
  }

  private async openXray(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
      return;
    }
    try {
      const panel = await host.xrayPanel();
      this.post({ type: 'xrayPanel', panel });
    } catch (err) {
      this.log(`[chat] xray/panel failed: ${String(err)}`);
    }
  }

  // ── @-mentions ──────────────────────────────────────────────────────────────

  /** Suggestions for the webview popup: open editors first, then a workspace glob. */
  private async mentionSuggestions(query: string): Promise<string[]> {
    const MAX = 12;
    const q = query.toLowerCase();
    const items = new Set<string>();

    for (const doc of vscode.workspace.textDocuments) {
      if (doc.uri.scheme !== 'file') {
        continue;
      }
      const rel = vscode.workspace.asRelativePath(doc.uri, false).replace(/\\/g, '/');
      if (!q || rel.toLowerCase().includes(q)) {
        items.add(rel);
      }
    }
    if (items.size < MAX && q.length >= 2) {
      try {
        const found = await vscode.workspace.findFiles(
          `**/*${query}*`,
          '**/{node_modules,bin,obj,out,dist,.git}/**',
          MAX,
        );
        for (const uri of found) {
          items.add(vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/'));
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
  private async expandMentions(prompt: string): Promise<{ text: string; paths: string[] }> {
    const root = vscode.workspace.workspaceFolders?.[0]?.uri;
    if (!root || !prompt.includes('@')) {
      return { text: prompt, paths: [] };
    }
    const MAX_FILES = 5;
    const MAX_CHARS = 40_000;
    const tokens = [...new Set([...prompt.matchAll(/@([^\s@]+)/g)].map((m) => m[1].replace(/[),.;:!?]+$/, '')))]
      .slice(0, MAX_FILES);

    let attachments = '';
    const paths: string[] = [];
    for (const token of tokens) {
      try {
        const doc = await vscode.workspace.openTextDocument(vscode.Uri.joinPath(root, token));
        let text = doc.getText();
        if (text.length > MAX_CHARS) {
          text = text.slice(0, MAX_CHARS) + '\n… [truncated]';
        }
        attachments += `\n\n## Attached file: ${token}\n\`\`\`\n${text}\n\`\`\``;
        paths.push(token);
      } catch {
        // not a workspace file — leave the token as plain text
      }
    }
    return { text: prompt + attachments, paths };
  }

  private denyAllPending(): void {
    for (const resolve of this.pendingApprovals.values()) {
      resolve(0);
    }
    this.pendingApprovals.clear();
  }

  private async send(text: string): Promise<void> {
    const prompt = text.trim();
    const host = this.getHost();
    if (!prompt || this.busy) {
      return;
    }
    if (!host?.isRunning) {
      // Two states, two remedies: with no folder open the host was never spawned, and
      // "Restart Host" cannot change that (hostStatus.ts).
      this.append({ role: 'error', text: hostUnavailableMessage(), timestamp: ChatViewProvider.now() });
      this.hydrate();
      promptOpenFolder();
      return;
    }

    // /xray opens the interactive panel (ephemeral overlay — no transcript entry, no model call).
    if (prompt.split(/\s+/, 1)[0].toLowerCase() === '/xray') {
      await this.openXray();
      return;
    }

    this.busy = true;
    this.streamText = '';
    this.plan = null;
    const timestamp = ChatViewProvider.now();
    this.append({ role: 'user', text: prompt, timestamp });
    const history = this.appendHistory(prompt);
    this.post({ type: 'turnStarted', prompt, timestamp, history });

    // In-place code actions (/fix /refactor /doc): rewrite the active editor's file (or
    // selection) with a native per-hunk preview — never sent to the chat history.
    const codeAction = ChatViewProvider.codeActionKind(prompt);
    if (codeAction) {
      try {
        await this.runCodeAction(codeAction, host);
      } finally {
        this.busy = false;
        this.autoSaveLast();
      }
      return;
    }

    // /explain and /review stay read-only and editor-side (VS parity): the active
    // document (or selection) is attached and the answer streams into the chat.
    const first = prompt.split(/\s+/, 1)[0].toLowerCase();
    if (first === '/explain' || first === '/review') {
      await this.runExplainReview(first === '/explain' ? 'explain' : 'review', host);
      return;
    }

    // Slash commands the host serves headlessly: rendered as an instant bubble (with
    // optional editor-side effects), never sent to the model. Unhandled ones fall through.
    if (prompt.startsWith('/')) {
      try {
        const slash = await host.commandSlash(prompt, this.historyEntries());
        if (slash.handled) {
          const outcome = await this.applySlashEffects(slash.effects ?? []);
          if (outcome.chatPrompt !== null) {
            // sendAsPrompt (expanded user template): continue as a normal chat turn.
            await this.chatTurn(outcome.chatPrompt, host);
            return;
          }
          const text = [slash.markdown ?? '', ...outcome.notes].filter((s) => s.length > 0).join('\n\n');
          this.finishTurn(text, null, false, 0);
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
        this.finishTurn('', ChatViewProvider.errorText(err), false, 0);
        return;
      }
    }

    await this.chatTurn(prompt, host);
  }

  /** Applies the editor-side effects of a handled slash command. */
  private async applySlashEffects(
    effects: SlashEffect[],
  ): Promise<{ chatPrompt: string | null; notes: string[]; rehydrate: boolean }> {
    let chatPrompt: string | null = null;
    const notes: string[] = [];
    let rehydrate = false;
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
            notes.push(vscode.l10n.t('📎 {0} attached to the next message.', name));
          }
          break;
        case 'copyToClipboard':
          await vscode.env.clipboard.writeText(e.value ?? '');
          break;
        case 'clearTranscript':
          this.transcript.length = 0;
          this.streamText = '';
          this.plan = null;
          this.promptTokens = 0;
          this.lastTokens = 0;
          rehydrate = true;
          break;
        case 'stateChange':
          if (e.name === 'model' && e.value) {
            this.model = e.value;
            await vscode.workspace.getConfiguration('inferpal').update('model', e.value, vscode.ConfigurationTarget.Workspace);
            rehydrate = true;
          }
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
        // /branch <n>: the host owns the fork but needs the full transcript (tool names and
        // timestamps live here, not in its API history).
        case 'branchRequest': {
          const note = await this.branchAtTurn(Number(e.value));
          if (note) {
            notes.push(note);
            rehydrate = true;
          }
          break;
        }
        // /branch <name>: switching branches is a plain session load (the host already
        // returned the localized bubble as markdown).
        case 'loadSession':
          await this.switchToSession(e.value ?? '');
          break;
        default:
          break; // unknown kinds are ignored (forward compatibility)
      }
    }
    return { chatPrompt, notes, rehydrate };
  }

  /** /explain and /review: active selection (or whole document) fenced into the prompt,
   * answered as a normal streamed chat turn. */
  private async runExplainReview(kind: 'explain' | 'review', host: HostClient): Promise<void> {
    const editor = this.getActiveEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      this.finishTurn('', vscode.l10n.t('Open a file in the editor to use /{0}.', kind), false, 0);
      return;
    }
    const doc = editor.document;
    const code = editor.selection.isEmpty ? doc.getText() : doc.getText(editor.selection);
    const file = vscode.workspace.asRelativePath(doc.uri, false);
    const instruction = kind === 'explain'
      ? vscode.l10n.t('Explain the following code from {0} — what it does, how, and any pitfalls.', file)
      : vscode.l10n.t('Review the following code from {0}: point out bugs, risks, and concrete improvements.', file);
    await this.chatTurn(`${instruction}\n\n\`\`\`\n${code}\n\`\`\``, host);
  }

  /** One model turn (agent or plain chat) with @-mention and pending-chip expansion. */
  private async chatTurn(prompt: string, host: HostClient): Promise<void> {
    try {
      const agentMode = vscode.workspace.getConfiguration('inferpal').get<boolean>('agentMode', true);
      const mentions = await this.expandMentions(prompt);
      let expanded = mentions.text;
      const attachedPaths = [...mentions.paths];
      if (this.pendingAttachments.length > 0) {
        for (const a of this.pendingAttachments) {
          expanded += `\n\n## Attached: ${a.name}\n\`\`\`\n${a.content}\n\`\`\``;
          attachedPaths.push(a.name);
        }
        this.pendingAttachments = [];
        this.postChips();
      }
      const result = await host.chatSend({
        prompt: expanded,
        model: this.model || undefined,
        agentMode,
        attachedPaths: attachedPaths.length > 0 ? attachedPaths : undefined,
      });
      const finalText = result.text || this.streamText;
      this.promptTokens = result.promptTokens || this.promptTokens;
      if (result.error) {
        this.append({ role: 'error', text: result.error, timestamp: ChatViewProvider.now() });
      } else {
        this.append({ role: 'assistant', text: finalText, timestamp: ChatViewProvider.now() });
      }
      this.busy = false;
      this.lastTokens = result.tokensUsed;
      this.post({
        type: 'turnEnded',
        text: finalText,
        error: result.error ?? null,
        cancelled: result.cancelled,
        tokens: result.tokensUsed,
        promptTokens: this.promptTokens,
        timestamp: ChatViewProvider.now(),
      });
      void this.pollBackendStatus(); // the turn may have loaded a model — refresh the VRAM badge
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
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
      });
    } finally {
      this.busy = false;
      this.autoSaveLast();
    }
  }

  /** Pushes an assistant/error entry and closes the turn in the webview. */
  /** Upper bound on live transcript entries (see {@link append}). */
  private static readonly MAX_TRANSCRIPT = 2000;

  /**
   * Appends to the transcript, keeping it bounded. A long-lived session (tool bubbles carry the
   * full command output) would otherwise grow without limit in memory, in every `hydrate` payload
   * and in the auto-saved session file. Trimming drops the oldest entries and leaves a marker —
   * never a silent disappearance.
   */
  private append(item: WvTranscriptItem): void {
    this.transcript.push(item);
    this.trimTranscript();
  }

  /** Drops the oldest entries past the cap, leaving a marker in their place. */
  private trimTranscript(): void {
    if (this.transcript.length <= ChatViewProvider.MAX_TRANSCRIPT) {
      return;
    }
    const dropped = this.transcript.length - ChatViewProvider.MAX_TRANSCRIPT;
    this.transcript.splice(0, dropped);
    this.transcript.unshift({
      role: 'error',
      text: vscode.l10n.t('{0} older messages were dropped to bound memory — the full conversation is in the saved session.', dropped),
      timestamp: ChatViewProvider.now(),
    });
  }

  /** Readable text for a failed RPC: JSON-RPC errors carry the host's message. */
  private static errorText(err: unknown): string {
    const raw = (err instanceof Error ? err.message : String(err)).trim();
    return raw.length > 0 ? raw : vscode.l10n.t('The command failed.');
  }

  private finishTurn(text: string, error: string | null, cancelled: boolean, tokens: number): void {
    const timestamp = ChatViewProvider.now();
    if (error) {
      this.append({ role: 'error', text: error, timestamp });
    } else if (text) {
      this.append({ role: 'assistant', text, timestamp });
    }
    this.busy = false;
    this.post({
      type: 'turnEnded',
      text: error ? '' : text,
      error,
      cancelled,
      tokens,
      promptTokens: this.promptTokens,
      timestamp,
    });
    this.autoSaveLast();
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
    await this.send(text);
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
   * VS Code side of the inline diff preview (ROADMAP 1.2.0 §1): the host runs the model step
   * (`codeAction/run`) and returns per-hunk offset edits; they are shown in the native
   * Refactor Preview (one confirmable entry per hunk — the ✓/✗ of the VS adornment).
   * Preview disabled (`inlineDiffPreviewEnabled: false`) ⇒ direct apply, one undo step.
   * Comfort only, never a control: these paths never went through approvals (VS parity).
   */
  private async runCodeAction(kind: 'fix' | 'refactor' | 'doc', host: HostClient): Promise<void> {
    const finish = (role: 'assistant' | 'error', text: string) =>
      this.finishTurn(role === 'error' ? '' : text, role === 'error' ? text : null, false, 0);

    const editor = this.getActiveEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      finish('error', vscode.l10n.t('Open a file in the editor to use /{0}.', kind));
      return;
    }

    const document = editor.document;
    const before = document.getText();
    this.post({ type: 'status', text: vscode.l10n.t('Running /{0} on {1}…', kind, vscode.workspace.asRelativePath(document.uri, false)) });

    let result: CodeActionResult;
    try {
      result = await host.codeActionRun({
        kind,
        text: before,
        selStart: document.offsetAt(editor.selection.start),
        selEnd: document.offsetAt(editor.selection.end),
        model: this.model || undefined,
      });
    } catch (err) {
      finish('error', err instanceof Error ? err.message : String(err));
      return;
    }

    if (result.outcome === 'noChange') {
      finish('assistant', vscode.l10n.t('Nothing to change — the code already looks good.'));
      return;
    }
    if (result.outcome !== 'edited' || result.edits.length === 0) {
      const base = vscode.l10n.t('The code action failed — check the backend connection and the model.');
      finish('error', result.failureDetail ? `${base}\n\n${result.failureDetail}` : base);
      return;
    }
    // The offsets were computed against the text we sent; a buffer that moved meanwhile
    // would misplace every hunk (same freshness guard as the VS renderer).
    if (document.getText() !== before) {
      finish('error', vscode.l10n.t('The document changed while the model was working — run /{0} again.', kind));
      return;
    }

    const preview = await this.inlineDiffPreviewEnabled(host);
    const edit = new vscode.WorkspaceEdit();
    for (const e of result.edits) {
      const range = new vscode.Range(document.positionAt(e.start), document.positionAt(e.end));
      if (preview) {
        edit.replace(document.uri, range, e.newText, {
          needsConfirmation: true,
          label: vscode.l10n.t('Inferpal /{0} — change {1}', kind, e.index),
        });
      } else {
        edit.replace(document.uri, range, e.newText);
      }
    }

    // With confirmable entries this opens the Refactor Preview and resolves on the user's
    // decision; false = discarded (or nothing left checked).
    const applied = await vscode.workspace.applyEdit(edit, { isRefactoring: true });
    if (!applied) {
      finish('assistant', vscode.l10n.t('Rewrite discarded — no changes were applied.'));
    } else if (preview) {
      finish('assistant', vscode.l10n.t('Rewrite applied from the preview — undo with Ctrl+Z if needed.'));
    } else {
      finish('assistant', vscode.l10n.t('Rewrite applied — undo with Ctrl+Z if needed.'));
    }
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
    const status = this.status ?? (this.getHost()?.isRunning ? null : { connected: false, vramBadge: '' });
    this.post({
      type: 'hydrate',
      transcript: this.transcript,
      models: this.models,
      model: this.model,
      busy: this.busy,
      stream: this.streamText,
      status,
      commands: this.commands,
      agentMode: vscode.workspace.getConfiguration('inferpal').get<boolean>('agentMode', true),
      contextWindow: this.contextWindow,
      promptTokens: this.promptTokens,
      lastTokens: this.lastTokens,
      plan: this.busy ? this.plan : null,
      history: this.historyEntries(),
      toolBubblesExpanded: this.toolBubblesExpanded,
      mentionCategories: this.mentionCats,
      chips: this.pendingAttachments.map((a) => ({ name: a.name })),
    });
  }

  private post(message: unknown): void {
    void this.view?.webview.postMessage(message);
  }

  private static now(): string {
    return new Date().toLocaleTimeString(vscode.env.language, { hour: '2-digit', minute: '2-digit' });
  }

  /** Localized strings injected into the webview (window.__l10n). Templates keep their
   * {0} placeholders — substitution happens webview-side (t()). */
}
