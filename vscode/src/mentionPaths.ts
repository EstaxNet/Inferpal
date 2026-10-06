// Which file an `@path` mention names, in a workspace that may hold several folders.
//
// ⚠ The picker and the expansion must agree on the form of a name. In a multi-root workspace the picker listed every
// folder's files relative to THEIR folder (`src/index.ts`) while the expansion read every name under the FIRST folder:
// picking another folder's `src/index.ts`, `README.md` or `package.json` attached the first folder's file of that
// name — another file, under the name the user picked — or nothing at all, without a word. The picker now writes the
// folder's name first when there are several (`asRelativePath`'s own default), and this reads it back.
//
// No `vscode` import: the rule is plain string work, runnable outside the editor.

/** A workspace folder as this rule sees it: its display name and where it is. */
export interface MentionFolder<T> {
  readonly name: string;
  readonly target: T;
}

/**
 * The folder a mention token belongs to and the path inside it. With several folders, a token that starts with a
 * folder's name and a slash belongs to that folder (the longest name wins, for a folder named like a sub-path of
 * another); any other token — typed by hand, or a single-folder workspace — is read under the first folder.
 */
export function resolveMention<T>(
  token: string,
  folders: readonly MentionFolder<T>[],
): { folder: MentionFolder<T>; relative: string } | undefined {
  if (folders.length === 0) {
    return undefined;
  }
  if (folders.length > 1) {
    const owner = [...folders]
      .sort((a, b) => b.name.length - a.name.length)
      .find((f) => f.name.length > 0 && token.startsWith(`${f.name}/`) && token.length > f.name.length + 1);
    if (owner) {
      return { folder: owner, relative: token.slice(owner.name.length + 1) };
    }
  }
  return { folder: folders[0], relative: token };
}

/**
 * The `@path` tokens a question names, in order, each once: `@src/app.ts`, or `@"docs/My Notes.md"` for a path that holds
 * a space — the form the picker writes then.
 *
 * ⚠ A bare token ends at the first space: read that way, `@docs/My Notes.md` named `docs/My`, which is no file, and the
 * file the person picked was not attached — with nothing said. Trailing punctuation of a bare token is the sentence's
 * (`see @src/app.ts.`); a quoted path is taken as written.
 */
export function mentionTokens(prompt: string): string[] {
  const tokens = [...prompt.matchAll(/@(?:"([^"\n]+)"|([^\s@"]+))/g)]
    .map((m) => m[1] ?? m[2].replace(/[),.;:!?]+$/, ''))
    .filter((token) => token.length > 0);
  return [...new Set(tokens)];
}

/** How the picker writes a path into the question: quoted when it holds a space, so `mentionTokens` reads it whole. */
export function mentionFor(path: string): string {
  return /\s/.test(path) ? `@"${path}"` : `@${path}`;
}
