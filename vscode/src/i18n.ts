// Extension-side translation that honours the language chosen in Inferpal's settings.
//
// vscode.l10n.t follows VS Code's display language only. The language picked in Inferpal — which the settings
// describe as overriding the editor's — reached the host's messages and nothing else, so the chat stayed in one
// language while the host answered in another. The keys and the bundles are unchanged
// (l10n/bundle.l10n.<code>.json, the English source string as the key): only the choice of bundle lives here.
import * as fs from 'fs';
import * as vscode from 'vscode';

/** undefined = follow VS Code (vscode.l10n.t); 'source' = English; otherwise the loaded bundle. */
let bundle: Record<string, string> | 'source' | undefined;
let current = '';

/**
 * Applies the language saved in Inferpal's settings: '' follows VS Code, 'en' is the English source, any other
 * code loads its bundle. Returns whether the language changed, so the caller re-renders only when needed.
 */
export function setLanguage(language: string | undefined, extensionUri: vscode.Uri): boolean {
  const code = (language ?? '').trim();
  if (code === current) {
    return false;
  }
  current = code;
  if (!code) {
    bundle = undefined;
  } else if (code.toLowerCase() === 'en') {
    bundle = 'source';
  } else {
    const file = vscode.Uri.joinPath(extensionUri, 'l10n', `bundle.l10n.${code.toLowerCase()}.json`).fsPath;
    try {
      bundle = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, '')) as Record<string, string>;
    } catch {
      // No bundle for that code: the editor's language is the best fallback left.
      bundle = undefined;
    }
  }
  return true;
}

/** Same contract as vscode.l10n.t: the English source string is the key, {0}-style arguments. */
export function t(message: string, ...args: Array<string | number | boolean>): string {
  if (bundle === undefined) {
    return vscode.l10n.t(message, ...args);
  }
  const text = bundle === 'source' ? message : bundle[message] ?? message;
  return text.replace(/\{(\d+)\}/g, (match, index: string) => {
    const value = args[Number(index)];
    return value === undefined ? match : String(value);
  });
}
