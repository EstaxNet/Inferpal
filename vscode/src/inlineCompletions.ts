// Ghost-text via the stable InlineCompletionItemProvider API, backed by the host's
// `fim/complete`. Replaces the whole VS MEF/adornment machinery with a debounced,
// cancellable request: VS Code's CancellationToken → `$/cancelRequest` → host
// CancellationToken → the LLM call aborts.
import * as vscode from 'vscode';
import { HostClient } from './hostClient';
import type { FimSettingsResult } from './protocol';

/** How long a read of Inferpal's inline-completion settings is reused. The provider runs on every
 * keystroke; a change saved in either settings window still reaches it within this delay. */
const SETTINGS_TTL_MS = 2000;
/** Context window around the caret (chars). Generous prefix, lighter suffix. */
const MAX_PREFIX_CHARS = 4000;
const MAX_SUFFIX_CHARS = 1500;
/** Documents above this size are skipped outright (getText cost + weak relevance). */
const MAX_DOC_CHARS = 500_000;

export class FimProvider implements vscode.InlineCompletionItemProvider {
  private settings: { value: FimSettingsResult | undefined; at: number } | undefined;

  constructor(
    private readonly getHost: () => HostClient | undefined,
    private readonly log: (line: string) => void,
  ) {}

  /** Inferpal's switch and the debounce of its mode (the Visual Studio leg reads the same two). A failed
   * read is kept for the same delay too, so it is logged once rather than on every keystroke. */
  private async readSettings(host: HostClient): Promise<FimSettingsResult | undefined> {
    const now = Date.now();
    if (this.settings && now - this.settings.at < SETTINGS_TTL_MS) {
      return this.settings.value;
    }
    let value: FimSettingsResult | undefined;
    try {
      value = await host.fimSettings();
    } catch (err) {
      this.log(`[fim] settings unavailable: ${String(err)}`);
    }
    this.settings = { value, at: now };
    return value;
  }

  async provideInlineCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    context: vscode.InlineCompletionContext,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[] | undefined> {
    if (!vscode.workspace.getConfiguration('inferpal').get<boolean>('fim.enabled', true)) {
      return undefined;
    }
    const host = this.getHost();
    if (!host?.isRunning || !host.info?.fim || host.isChatBusy) {
      return undefined;
    }
    if (document.uri.scheme !== 'file') {
      return undefined;
    }

    // Unchecked in Inferpal's settings: no request at all, as in Visual Studio.
    const settings = await this.readSettings(host);
    if (!settings?.enabled) {
      return undefined;
    }

    // Debounce inside the provider (VS Code calls it on every keystroke), with the mode's delay.
    await delay(settings.debounceMs);
    if (token.isCancellationRequested) {
      return undefined;
    }

    // The size comes from the last position and only the two slices are built: `getText()` built
    // the whole document (up to MAX_DOC_CHARS) on every pause to keep 5 500 characters of it.
    const offset = document.offsetAt(position);
    const length = document.offsetAt(document.lineAt(document.lineCount - 1).range.end);
    if (length > MAX_DOC_CHARS) {
      return undefined;
    }
    const prefix = document.getText(
      new vscode.Range(document.positionAt(Math.max(0, offset - MAX_PREFIX_CHARS)), position));
    const suffix = document.getText(
      new vscode.Range(position, document.positionAt(offset + MAX_SUFFIX_CHARS)));

    let completion: string;
    try {
      completion = await host.fimComplete({ prefix, suffix }, token);
    } catch (err) {
      if (!token.isCancellationRequested) {
        this.log(`[fim] failed: ${String(err)}`);
      }
      return undefined;
    }
    if (token.isCancellationRequested || !completion || completion.trim().length === 0) {
      return undefined;
    }

    // When the IntelliSense widget is open, the ghost text must extend what the
    // widget already shows, otherwise VS Code discards it silently — bail early
    // instead of rendering nothing.
    const selected = context.selectedCompletionInfo;
    if (selected) {
      const typed = document.getText(selected.range);
      const expected = selected.text;
      if (!expected.startsWith(typed) || !(typed + completion).startsWith(expected)) {
        return undefined;
      }
    }

    return [new vscode.InlineCompletionItem(completion, new vscode.Range(position, position))];
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
