// Editor side of the reverse-RPC surface: answers the host's editor/* requests and
// mirrors open documents into the host's dirty-buffer overlay (textDocument/did*).
import * as vscode from 'vscode';
import { HostClient, EditorDelegate } from './hostClient';
import { ActiveDocumentDto, ApprovalAnswer, ApprovalNote, EditResultDto } from './protocol';

/** Documents above this size are not mirrored (the host reads them from disk instead). */
const MAX_MIRRORED_BYTES = 1024 * 1024;
/** Full-text didChange is debounced per document to keep typing cheap. */
const CHANGE_DEBOUNCE_MS = 300;

function isMirrorable(doc: vscode.TextDocument): boolean {
  return doc.uri.scheme === 'file' && Buffer.byteLength(doc.getText(), 'utf8') <= MAX_MIRRORED_BYTES;
}

/**
 * Tracks the last active *text* editor. `window.activeTextEditor` becomes undefined
 * the moment focus moves to the Inferpal webview, so the answer to the host's
 * `editor/activeDocument` must come from this cache — the last real editor is what
 * the user means by "the active file".
 */
export class EditorBridge implements EditorDelegate, vscode.Disposable {
  private lastActive: vscode.TextEditor | undefined;
  private readonly changeTimers = new Map<string, NodeJS.Timeout>();
  private readonly disposables: vscode.Disposable[] = [];
  private host: HostClient | undefined;
  private approvalCard: ((message: string) => Promise<number | undefined>) | undefined;

  constructor(private readonly secrets?: vscode.SecretStorage) {
    this.lastActive = vscode.window.activeTextEditor;
  }

  /**
   * Encrypts an MCP OAuth payload through the editor's secret store — the OS keychain
   * (libsecret / Keychain / DPAPI). Only non-Windows hosts ask: Windows hosts use DPAPI directly
   * so the token file stays shared with the Visual Studio extension.
   *
   * The value handed back is a receipt, not the secret: the payload itself lives in SecretStorage
   * under `key`, and the host only persists this marker next to its config.
   */
  async protectSecret(key: string, value: string): Promise<string> {
    if (!this.secrets) {
      throw new Error('No secret storage available in this editor session.');
    }
    await this.secrets.store(key, value);
    return `vscode-secret:${key}`;
  }

  /** Reverse of {@link protectSecret}; throws when the receipt is unknown (secret store cleared). */
  async unprotectSecret(key: string, receipt: string): Promise<string> {
    if (!this.secrets) {
      throw new Error('No secret storage available in this editor session.');
    }
    if (receipt !== `vscode-secret:${key}`) {
      throw new Error('Unknown secret receipt — the stored token cannot be read back.');
    }
    const value = await this.secrets.get(key);
    if (value === undefined) {
      throw new Error('The stored MCP token is gone from the secret store — re-authorize.');
    }
    return value;
  }

  /** Preferred approval UI (chat webview card); modal dialog stays as the fallback. */
  setApprovalCard(handler: (message: string) => Promise<number | undefined>): void {
    this.approvalCard = handler;
  }

  /** Called once the host is running; seeds the overlay with already-open documents. */
  attach(host: HostClient): void {
    this.host = host;

    for (const doc of vscode.workspace.textDocuments) {
      if (isMirrorable(doc)) {
        host.didOpen({ path: doc.uri.fsPath, text: doc.getText() });
      }
    }
    if (this.lastActive?.document.uri.scheme === 'file') {
      host.didChangeActiveDocument(this.lastActive.document.uri.fsPath);
    }

    this.disposables.push(
      vscode.window.onDidChangeActiveTextEditor((editor) => {
        if (!editor) {
          return; // webview/panel focus — keep the last real editor
        }
        this.lastActive = editor;
        if (editor.document.uri.scheme === 'file') {
          this.host?.didChangeActiveDocument(editor.document.uri.fsPath);
        }
      }),
      vscode.workspace.onDidOpenTextDocument((doc) => {
        if (isMirrorable(doc)) {
          this.host?.didOpen({ path: doc.uri.fsPath, text: doc.getText() });
        }
      }),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (!isMirrorable(e.document)) {
          return;
        }
        const key = e.document.uri.fsPath;
        clearTimeout(this.changeTimers.get(key));
        this.changeTimers.set(
          key,
          setTimeout(() => {
            this.changeTimers.delete(key);
            this.host?.didChange({ path: key, text: e.document.getText() });
          }, CHANGE_DEBOUNCE_MS),
        );
      }),
      vscode.workspace.onDidCloseTextDocument((doc) => {
        if (doc.uri.scheme === 'file') {
          const key = doc.uri.fsPath;
          clearTimeout(this.changeTimers.get(key));
          this.changeTimers.delete(key);
          this.host?.didClose(key);
        }
      }),
    );
  }

  // ── EditorDelegate (host → editor requests) ────────────────────────────────

  async approvalRequest(note: ApprovalNote): Promise<number> {
    // Chat card first (Continue/Cline style) — inline, keeps the flow readable.
    if (this.approvalCard) {
      try {
        const answer = await this.approvalCard(note.message);
        if (answer !== undefined) {
          return answer;
        }
      } catch {
        // card path failed — fall through to the modal
      }
    }
    // Modal fallback so an agent mid-run can never be silently ignored; Esc/close = deny.
    const once = vscode.l10n.t('Allow once');
    const always = vscode.l10n.t('Always allow (session)');
    const answer = await vscode.window.showWarningMessage(note.message, { modal: true }, once, always);
    if (answer === once) {
      return ApprovalAnswer.Once;
    }
    if (answer === always) {
      return ApprovalAnswer.Always;
    }
    return ApprovalAnswer.Deny;
  }

  async activeDocument(): Promise<ActiveDocumentDto> {
    const editor = this.currentEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      return { path: null, text: null };
    }
    return { path: editor.document.uri.fsPath, text: editor.document.getText() };
  }

  async insertAtCursor(text: string): Promise<string | null> {
    const editor = this.currentEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      return null;
    }
    try {
      const ok = await editor.edit((builder) => builder.insert(editor.selection.active, text));
      return ok ? editor.document.uri.fsPath : null;
    } catch {
      return null; // cached editor got disposed between focus changes — best-effort contract
    }
  }

  async replaceSelection(text: string): Promise<EditResultDto> {
    const editor = this.currentEditor();
    if (!editor || editor.document.uri.scheme !== 'file') {
      return { path: null, replacedSelection: false };
    }
    const hadSelection = !editor.selection.isEmpty;
    try {
      const ok = await editor.edit((builder) => {
        if (hadSelection) {
          builder.replace(editor.selection, text);
        } else {
          builder.insert(editor.selection.active, text);
        }
      });
      return ok
        ? { path: editor.document.uri.fsPath, replacedSelection: hadSelection }
        : { path: null, replacedSelection: false };
    } catch {
      return { path: null, replacedSelection: false }; // disposed editor — best-effort contract
    }
  }

  async editorDiagnostics(): Promise<string | null> {
    // Problems panel across all open files — errors and warnings only (info/hint noise
    // would eat the agent's context), workspace-relative paths, hard cap on volume.
    const MAX_LINES = 200;
    const lines: string[] = [];
    let dropped = 0;
    for (const [uri, diags] of vscode.languages.getDiagnostics()) {
      if (uri.scheme !== 'file') {
        continue;
      }
      const rel = vscode.workspace.asRelativePath(uri, false);
      for (const d of diags) {
        if (d.severity !== vscode.DiagnosticSeverity.Error && d.severity !== vscode.DiagnosticSeverity.Warning) {
          continue;
        }
        if (lines.length >= MAX_LINES) {
          dropped++;
          continue;
        }
        const sev = d.severity === vscode.DiagnosticSeverity.Error ? 'error' : 'warning';
        const code = typeof d.code === 'object' ? d.code.value : d.code ?? '';
        const src = d.source ? `${d.source} ` : '';
        lines.push(`${rel}(${d.range.start.line + 1},${d.range.start.character + 1}): ${sev} ${src}${code}: ${d.message.replace(/\s+/g, ' ')}`);
      }
    }
    if (lines.length === 0) {
      return null; // clean panel proves nothing about unopened files — let the host build
    }
    if (dropped > 0) {
      lines.push(`… ${dropped} more diagnostics truncated.`);
    }
    return lines.join('\n');
  }

  /** Last real text editor (live or cached) — what the code actions operate on:
   * focus sits in the chat webview when a slash command is typed, so the cache is
   * the only truthful answer to "the file the user means". */
  activeEditor(): vscode.TextEditor | undefined {
    return this.currentEditor();
  }

  // ── Internals ──────────────────────────────────────────────────────────────

  /** Live active editor when there is one, else the cached last active editor
   * (still valid unless its document was closed since). */
  private currentEditor(): vscode.TextEditor | undefined {
    const live = vscode.window.activeTextEditor;
    if (live) {
      return live;
    }
    if (this.lastActive && !this.lastActive.document.isClosed) {
      return this.lastActive;
    }
    return undefined;
  }

  dispose(): void {
    for (const timer of this.changeTimers.values()) {
      clearTimeout(timer);
    }
    this.changeTimers.clear();
    for (const d of this.disposables) {
      d.dispose();
    }
  }
}
