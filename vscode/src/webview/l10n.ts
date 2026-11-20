// Webview-side localization: the extension host injects `window.__l10n` (a key → translated
// string map built with the extension's translator, src/i18n.ts) into the page; this helper reads it with {0}-style
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

/** The interface's language, as the extension wrote it on the page (`<html lang>`): numbers are written in it, as the
 *  words are. Without it, toLocaleString() follows the browser's own locale — "32,664" under French words. A tag the page
 *  could not use as a locale is dropped: toLocaleString would throw on it. */
export const PAGE_LOCALE: string | undefined = (() => {
  const lang = document.documentElement.lang;
  try {
    return lang ? Intl.getCanonicalLocales(lang)[0] : undefined;
  } catch {
    return undefined;
  }
})();
