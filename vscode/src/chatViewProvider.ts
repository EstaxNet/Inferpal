// Sidebar chat: WebviewViewProvider + typed postMessage protocol. The extension host
// (this class) is the source of truth for the transcript — the webview can be destroyed
// on hide and is re-hydrated on every resolveWebviewView.
import * as vscode from 'vscode';
import { HostClient } from './hostClient';
import { CodeActionResult, SavedMessage } from './protocol';

interface TranscriptItem {
  role: 'user' | 'assistant' | 'status' | 'tool' | 'error';
  text: string;
  detail?: string;
}

/** Messages webview → extension. */
type InboundMessage =
  | { type: 'ready' }
  | { type: 'send'; text: string }
  | { type: 'cancel' }
  | { type: 'reset' }
  | { type: 'pickModel'; model: string }
  | { type: 'approvalAnswer'; id: number; answer: number }
  | { type: 'mentionQuery'; query: string }
  | { type: 'openApprovalDiff'; text: string };

export class ChatViewProvider implements vscode.WebviewViewProvider {
  public static readonly viewId = 'inferpal.chat';

  private view: vscode.WebviewView | undefined;
  private readonly transcript: TranscriptItem[] = [];
  private models: string[] = [];
  private model = '';
  private busy = false;
  private streamText = '';
  private approvalSeq = 0;
  private readonly pendingApprovals = new Map<number, (answer: number) => void>();

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly getHost: () => HostClient | undefined,
    private readonly getActiveEditor: () => vscode.TextEditor | undefined,
    private readonly log: (line: string) => void,
  ) {}

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    view.webview.options = {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(this.extensionUri, 'media')],
    };
    view.webview.html = this.renderHtml(view.webview);
    view.webview.onDidReceiveMessage((msg: InboundMessage) => this.onMessage(msg));
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
  async requestApproval(message: string): Promise<number | undefined> {
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
    this.post({ type: 'approval', id, message });
    return answer;
  }

  /** New conversation: clears both the host history and the local transcript. */
  async resetConversation(): Promise<void> {
    const host = this.getHost();
    if (host?.isRunning) {
      try {
        await host.chatReset();
      } catch (err) {
        this.log(`[chat] reset failed: ${String(err)}`);
      }
    }
    this.transcript.length = 0;
    this.streamText = '';
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
      onPlan: (plan) => this.post({ type: 'status', text: `Plan: ${plan.goal} (${plan.steps.length} steps)` }),
      onTool: (tool) => {
        // detail keeps the output: that's what a restored session must feed the model.
        this.transcript.push({ role: 'tool', text: tool.name, detail: tool.output });
        this.post({ type: 'tool', name: tool.name, input: tool.input });
      },
      onStreamReset: () => {
        this.streamText = '';
        this.post({ type: 'streamReset' });
      },
    });

    this.model = vscode.workspace.getConfiguration('inferpal').get<string>('model', '') || host.info?.defaultModel || '';
    try {
      this.models = await host.modelsList();
    } catch (err) {
      this.log(`[chat] models/list failed: ${String(err)}`);
      this.models = this.model ? [this.model] : [];
    }

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

  // ── Sessions ────────────────────────────────────────────────────────────────

  /** Persistable view of the transcript (same shape the VS extension saves). */
  private snapshot(): SavedMessage[] {
    return this.transcript.map((item) =>
      item.role === 'tool'
        ? { role: 'tool', content: item.detail ?? '', toolName: item.text }
        : { role: item.role, content: item.text },
    );
  }

  /** Replaces the transcript with a restored session (host history already rebuilt). */
  private applySession(messages: SavedMessage[]): void {
    this.transcript.length = 0;
    for (const m of messages) {
      if (m.role === 'tool') {
        this.transcript.push({ role: 'tool', text: m.toolName ?? 'tool', detail: m.content });
      } else if (m.role === 'user' || m.role === 'assistant' || m.role === 'error') {
        this.transcript.push({ role: m.role, text: m.content });
      }
    }
    this.streamText = '';
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

  /** Command: save the conversation under a user-chosen name. */
  async saveSessionCommand(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning || this.transcript.length === 0) {
      return;
    }
    const suggested = this.transcript.find((m) => m.role === 'user')?.text.slice(0, 35) ?? '';
    const name = await vscode.window.showInputBox({
      prompt: vscode.l10n.t('Session name'),
      value: suggested.replace(/[\\/:*?"<>|\n]+/g, ' ').trim(),
    });
    if (!name) {
      return;
    }
    await host.sessionSave(name, this.snapshot());
    void vscode.window.showInformationMessage(vscode.l10n.t('Session saved: {0}', name));
  }

  /** Command: pick a saved session and restore it (host history + transcript). */
  async loadSessionCommand(): Promise<void> {
    const host = this.getHost();
    if (!host?.isRunning) {
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
      return;
    }
    const pick = await this.pickSession(vscode.l10n.t('Pick a session to delete'));
    if (pick) {
      await host.sessionDelete(pick);
    }
  }

  private async pickSession(placeholder: string): Promise<string | undefined> {
    const host = this.getHost()!;
    const sessions = await host.sessionList();
    if (sessions.length === 0) {
      void vscode.window.showInformationMessage(vscode.l10n.t('No saved sessions.'));
      return undefined;
    }
    const picked = await vscode.window.showQuickPick(
      sessions.map((s) => ({
        label: s.name,
        description: `${s.messageCount} msg`,
        detail: s.preview,
      })),
      { placeHolder: placeholder },
    );
    return picked?.label;
  }

  // ── Webview → extension ─────────────────────────────────────────────────────

  private async onMessage(msg: InboundMessage): Promise<void> {
    switch (msg.type) {
      case 'ready':
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
      case 'pickModel':
        this.model = msg.model;
        await vscode.workspace.getConfiguration('inferpal').update('model', msg.model, vscode.ConfigurationTarget.Workspace);
        return;
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
  private async expandMentions(prompt: string): Promise<string> {
    const root = vscode.workspace.workspaceFolders?.[0]?.uri;
    if (!root || !prompt.includes('@')) {
      return prompt;
    }
    const MAX_FILES = 5;
    const MAX_CHARS = 40_000;
    const tokens = [...new Set([...prompt.matchAll(/@([^\s@]+)/g)].map((m) => m[1].replace(/[),.;:!?]+$/, '')))]
      .slice(0, MAX_FILES);

    let attachments = '';
    for (const token of tokens) {
      try {
        const doc = await vscode.workspace.openTextDocument(vscode.Uri.joinPath(root, token));
        let text = doc.getText();
        if (text.length > MAX_CHARS) {
          text = text.slice(0, MAX_CHARS) + '\n… [truncated]';
        }
        attachments += `\n\n## Attached file: ${token}\n\`\`\`\n${text}\n\`\`\``;
      } catch {
        // not a workspace file — leave the token as plain text
      }
    }
    return prompt + attachments;
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
      this.transcript.push({ role: 'error', text: vscode.l10n.t('Inferpal host is not running — use "Inferpal: Restart Host".') });
      this.hydrate();
      return;
    }

    this.busy = true;
    this.streamText = '';
    this.transcript.push({ role: 'user', text: prompt });
    this.post({ type: 'turnStarted', prompt });

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

    // Slash commands the host serves headlessly (/replay, /xray, …): rendered as an
    // instant bubble, never sent to the model. Unhandled ones fall through to chat.
    if (prompt.startsWith('/')) {
      try {
        const slash = await host.commandSlash(prompt);
        if (slash.handled) {
          const text = slash.markdown ?? '';
          this.transcript.push({ role: 'assistant', text });
          this.post({ type: 'turnEnded', text, error: null, cancelled: false, tokens: 0 });
          this.busy = false;
          return;
        }
      } catch (err) {
        this.log(`[chat] command/slash failed: ${String(err)}`);
        // fall through — the prompt is sent as a normal chat turn
      }
    }

    try {
      const agentMode = vscode.workspace.getConfiguration('inferpal').get<boolean>('agentMode', true);
      const expanded = await this.expandMentions(prompt);
      const result = await host.chatSend({
        prompt: expanded,
        model: this.model || undefined,
        agentMode,
      });
      const finalText = result.text || this.streamText;
      if (result.error) {
        this.transcript.push({ role: 'error', text: result.error });
      } else {
        this.transcript.push({ role: 'assistant', text: finalText });
      }
      this.post({
        type: 'turnEnded',
        text: finalText,
        error: result.error ?? null,
        cancelled: result.cancelled,
        tokens: result.tokensUsed,
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      this.transcript.push({ role: 'error', text: message });
      this.post({ type: 'turnEnded', text: this.streamText, error: message, cancelled: false, tokens: 0 });
    } finally {
      this.busy = false;
      this.autoSaveLast();
    }
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
    const finish = (role: 'assistant' | 'error', text: string) => {
      this.transcript.push({ role, text });
      this.post({ type: 'turnEnded', text, error: role === 'error' ? text : null, cancelled: false, tokens: 0 });
    };

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
    this.post({
      type: 'hydrate',
      transcript: this.transcript,
      models: this.models,
      model: this.model,
      busy: this.busy,
      stream: this.streamText,
    });
  }

  private post(message: unknown): void {
    void this.view?.webview.postMessage(message);
  }

  private renderHtml(webview: vscode.Webview): string {
    const script = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'chat.js'));
    const style = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'chat.css'));
    const nonce = Array.from({ length: 24 }, () => Math.floor(Math.random() * 36).toString(36)).join('');
    // CSP: no inline scripts, assets restricted to this extension's media/ folder.
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource};">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>Inferpal</title>
</head>
<body>
<div id="messages"></div>
<div id="composer">
  <div id="statusline" hidden></div>
  <textarea id="prompt" rows="3" placeholder="Ask Inferpal…"></textarea>
  <div id="toolbar">
    <select id="model" title="Model"></select>
    <button id="reset" title="New conversation">✚</button>
    <button id="cancel" title="Cancel" hidden>◼</button>
    <button id="send" title="Send">➤</button>
  </div>
</div>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }
}
