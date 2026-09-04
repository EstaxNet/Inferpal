// Webview-side localization: the extension host injects `window.__l10n` (a key → translated
// string map built with vscode.l10n.t) into the page; this helper reads it with {0}-style
// placeholder substitution. No literal English belongs in src/webview/ — every user-visible
// string must go through t() with a key served by chatViewProvider.webviewStrings().
const dict: Record<string, string> = window.__l10n ?? {};

export function t(key: string, ...args: (string | number)[]): string {
  return fill(dict[key] ?? key, ...args);
}

/** {0}-style substitution on a string that is already localized — the settings panel needs it for
 *  the labels and messages it gets from the host (`settings/strings`, the same .resx as VS)
 *  instead of from this dictionary. One substitution, one implementation. */
export function fill(text: string, ...args: (string | number)[]): string {
  for (let i = 0; i < args.length; i++) {
    text = text.split(`{${i}}`).join(String(args[i]));
  }
  return text;
}
