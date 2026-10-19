import * as path from 'path';

/**
 * Which of the repositories VS Code opened holds `root`: the deepest one whose folder contains it — a submodule or a
 * nested repository before the one around it, as git itself picks when the host runs it in `root` — else -1. Listing
 * another repository's commits would offer hashes the host's git cannot find.
 * ⚠ Across drives on Windows `path.relative` returns an absolute path, which is never "inside".
 */
export function repositoryHolding(repositoryRoots: string[], root: string | undefined, p: path.PlatformPath = path): number {
  if (root === undefined) {
    return -1;
  }
  let best = -1;
  repositoryRoots.forEach((repository, i) => {
    const rel = p.relative(repository, root);
    const inside = rel === '' || (rel !== '..' && !rel.startsWith('..' + p.sep) && !p.isAbsolute(rel));
    if (inside && (best < 0 || repository.length > repositoryRoots[best].length)) {
      best = i;
    }
  });
  return best;
}

/**
 * The typed text the picker offers as its own entry, `/check <text>`, above the commits it filters: what Enter runs is
 * always the entry shown highlighted, never a commit chosen behind the text (a branch named `cafe` typed over a hash
 * that contains it). None when nothing is typed; when the text holds a space, which no revision does (it is words
 * of a commit message, searched); or for `init`: `/check init` writes an example check into the repository, the one
 * meaning of `/check` that is not a review.
 */
export function typedRevision(value: string): string | undefined {
  const text = value.trim();
  return text === '' || /\s/.test(text) || text.toLowerCase() === 'init' ? undefined : text;
}
