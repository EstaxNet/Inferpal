// Webview-side localization: the extension host injects `window.__l10n` (a key → translated
// string map built with vscode.l10n.t) into the page; this helper reads it with {0}-style
// placeholder substitution. No literal English belongs in src/webview/ — every user-visible
// string must go through t() with a key served by chatViewProvider.webviewStrings().
const dict: Record<string, string> = window.__l10n ?? {};

export function t(key: string, ...args: (string | number)[]): string {
  let text = dict[key] ?? key;
  for (let i = 0; i < args.length; i++) {
    text = text.split(`{${i}}`).join(String(args[i]));
  }
  return text;
}
