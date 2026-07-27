// Ghost-text via the stable InlineCompletionItemProvider API, backed by the host's
// `fim/complete`. Replaces the whole VS MEF/adornment machinery with a debounced,
// cancellable request: VS Code's CancellationToken → `$/cancelRequest` → host
// CancellationToken → the LLM call aborts.
import * as vscode from 'vscode';
import { HostClient } from './hostClient';

/** Idle time before the request fires; typing again cancels the pending one. */
const DEBOUNCE_MS = 200;
/** Context window around the caret (chars). Generous prefix, lighter suffix. */
const MAX_PREFIX_CHARS = 4000;
const MAX_SUFFIX_CHARS = 1500;
/** Documents above this size are skipped outright (getText cost + weak relevance). */
const MAX_DOC_CHARS = 500_000;

export class FimProvider implements vscode.InlineCompletionItemProvider {
  constructor(
    private readonly getHost: () => HostClient | undefined,
    private readonly log: (line: string) => void,
  ) {}

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
    if (document.uri.scheme !== 'file' || document.getText().length > MAX_DOC_CHARS) {
      return undefined;
    }

    // Debounce inside the provider (VS Code calls it on every keystroke).
    await delay(DEBOUNCE_MS);
    if (token.isCancellationRequested) {
      return undefined;
    }

    const offset = document.offsetAt(position);
    const text = document.getText();
    const prefix = text.slice(Math.max(0, offset - MAX_PREFIX_CHARS), offset);
    const suffix = text.slice(offset, offset + MAX_SUFFIX_CHARS);

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
