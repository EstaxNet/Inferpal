// The question a bubble holds, and the names of what went with it: the inverse of the line the extension writes under a
// question ("📎 Attached: a · b"). The same rule as the Core's ChatTurnPolicy.SplitBubbleText: the LAST recap, nothing
// after it on its line, and a blank line before it unless it is the whole bubble — a question that quotes the recap's
// words mid-text is never cut.
//
// No `vscode` import and no `t()`: the webview and the extension both use it, and the recap's template is the caller's.

/** The bubble's words and the names its recap lists; no names when the bubble has no recap this rule recognises. */
export function splitBubbleText(bubble: string, recapTemplate: string): { text: string; names: string[] } {
  const prefix = recapTemplate.split('{0}')[0];
  if (!prefix) {
    return { text: bubble, names: [] };
  }
  const at = bubble.lastIndexOf(prefix);
  if (at < 0 || bubble.indexOf('\n', at) >= 0) {
    return { text: bubble, names: [] };
  }
  if (at > 0 && !bubble.slice(0, at).endsWith('\n\n')) {
    return { text: bubble, names: [] };
  }
  const names = bubble.slice(at + prefix.length).split(' · ').filter((n) => n.length > 0);
  return names.length === 0 ? { text: bubble, names: [] } : { text: bubble.slice(0, at).replace(/\n+$/, ''), names };
}
