// Ghost-text via the stable InlineCompletionItemProvider API, backed by the host's
// `fim/complete` (a pause in typing) and `fim/ask` (the key). Replaces the whole VS MEF/adornment machinery with a debounced,
// cancellable request: VS Code's CancellationToken → `$/cancelRequest` → host
// CancellationToken → the LLM call aborts.
import * as vscode from 'vscode';
import { HostClient } from './hostClient';
import { hostErrorText, hostUnavailableMessage } from './hostStatus';
import { t } from './i18n';
import type { FimAskResult, FimSettingsResult } from './protocol';

/** How long a read of Inferpal's inline-completion settings is reused. The provider runs on every
 * keystroke; a change saved in either settings window still reaches it within this delay. */
const SETTINGS_TTL_MS = 2000;
/** Context window around the caret (chars). Generous prefix, lighter suffix. ⚠ The same numbers as Visual Studio's
 * ghost text (FimContextBuilder.MaxPrefixChars / MaxSuffixChars), held by FimContextWindowTests. */
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
    // Asked for by its key (Alt+', or VS Code's own "Trigger Inline Suggestion"), not by a pause in typing: it goes at
    // once, and a key that brings nothing says why — a pause that brings nothing stays quiet, it comes back every pause.
    const asked = context.triggerKind === vscode.InlineCompletionTriggerKind.Invoke;
    // A pause completes saved files only; the key also completes an untitled buffer, which has no path to send. Nothing
    // else: a commit message box, a comment widget or a notebook's output is text no ignore file can judge.
    if (document.uri.scheme !== 'file' && !(asked && document.uri.scheme === 'untitled')) {
      return undefined;
    }
    if (!vscode.workspace.getConfiguration('inferpal').get<boolean>('fim.enabled', true)) {
      if (asked) {
        say(t('Inferpal: inline completion is turned off in VS Code\'s settings (inferpal.fim.enabled).'));
      }
      return undefined;
    }
    const host = this.getHost();
    if (!host?.isRunning) {
      if (asked) {
        say(hostUnavailableMessage());
      }
      return undefined;
    }
    if (asked) {
      return this.ask(host, document, position, context, token);
    }
    if (!host.info?.fim) {
      return undefined;
    }

    // Unchecked in Inferpal's settings, or only on demand: a pause asks for nothing, as in Visual Studio.
    const settings = await this.readSettings(host);
    if (!settings?.enabled || settings.onDemand) {
      return undefined;
    }

    // Debounce inside the provider (VS Code calls it on every keystroke), with the mode's delay.
    await delay(settings.debounceMs);
    if (token.isCancellationRequested) {
      return undefined;
    }
    // A chat answering on the completion server's machine holds its card: no request then. A completion server on
    // another machine keeps completing.
    if (host.isChatBusy && settings.yieldsToChat !== false) {
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
      // The file goes with it: one the workspace's ignore files exclude is never completed.
      completion = await host.fimComplete({ prefix, suffix, path: document.uri.scheme === 'file' ? document.uri.fsPath : undefined }, token);
    } catch (err) {
      if (!token.isCancellationRequested) {
        this.log(`[fim] failed: ${String(err)}`);
      }
      return undefined;
    }
    if (token.isCancellationRequested || !completion || completion.trim().length === 0) {
      return undefined;
    }

    if (!fitsTheList(document, context, completion)) {
      return undefined;
    }
    return [new vscode.InlineCompletionItem(completion, new vscode.Range(position, position))];
  }

  /** The key's request: no pause, no size cut (only the two slices are read), and every empty end said — the sentence
   * the host decided (`fim/ask`), or the reason VS Code itself would drop the suggestion. */
  private async ask(
    host: HostClient,
    document: vscode.TextDocument,
    position: vscode.Position,
    context: vscode.InlineCompletionContext,
    token: vscode.CancellationToken,
  ): Promise<vscode.InlineCompletionItem[] | undefined> {
    const offset = document.offsetAt(position);
    const prefix = document.getText(
      new vscode.Range(document.positionAt(Math.max(0, offset - MAX_PREFIX_CHARS)), position));
    const suffix = document.getText(
      new vscode.Range(position, document.positionAt(offset + MAX_SUFFIX_CHARS)));
    let result: FimAskResult;
    try {
      const path = document.uri.scheme === 'file' ? document.uri.fsPath : undefined;
      result = await host.fimAsk({ prefix, suffix, path }, token);
    } catch (err) {
      if (!token.isCancellationRequested) {
        this.log(`[fim] ask failed: ${String(err)}`);
        say(hostErrorText(err));
      }
      return undefined;
    }
    if (token.isCancellationRequested) {
      return undefined;
    }
    if (!result.text) {
      if (result.notice) {
        say(result.notice);
      }
      return undefined;
    }
    if (!fitsTheList(document, context, result.text)) {
      say(t('Inferpal: the suggestion does not continue the item selected in the list — close the list (Esc) and ask again.'));
      return undefined;
    }
    return [new vscode.InlineCompletionItem(result.text, new vscode.Range(position, position))];
  }
}

/** When the IntelliSense widget is open, the ghost text must extend what the widget already shows, otherwise VS Code
 * discards it silently — bail early instead of rendering nothing. */
function fitsTheList(document: vscode.TextDocument, context: vscode.InlineCompletionContext, completion: string): boolean {
  const selected = context.selectedCompletionInfo;
  if (!selected) {
    return true;
  }
  const typed = document.getText(selected.range);
  return selected.text.startsWith(typed) && (typed + completion).startsWith(selected.text);
}

/** A sentence in the status bar, long enough to be read: the key that asked waits for an answer there. */
function say(text: string): void {
  vscode.window.setStatusBarMessage(text, 6000);
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
