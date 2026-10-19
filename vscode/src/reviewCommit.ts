import * as vscode from 'vscode';
import { t } from './i18n';
import { workspaceRoot } from './hostStatus';
import { repositoryHolding, typedRevision } from './reviewCommitPick';

/** A commit of the built-in git extension's log, as the picker shows it. */
interface LoggedCommit {
  hash: string;
  message: string;
}

/**
 * The commits of the repository that holds the workspace root, newest first, as the built-in git extension lists
 * them; [] without it, or when no repository it opened holds the root.
 */
async function recentCommits(): Promise<LoggedCommit[]> {
  try {
    const extension = vscode.extensions.getExtension('vscode.git');
    const exported = extension?.isActive ? extension.exports : await extension?.activate();
    const api = exported?.getAPI?.(1);
    const repositories: { rootUri: vscode.Uri; log: (o: { maxEntries: number }) => Promise<unknown> }[] =
      api?.repositories ?? [];
    const repository = repositories[repositoryHolding(repositories.map((r) => r.rootUri.fsPath), workspaceRoot())];
    const log = await repository?.log({ maxEntries: 30 });
    return Array.isArray(log)
      ? log
          .filter((c) => typeof c?.hash === 'string')
          .map((c) => ({ hash: c.hash as string, message: String(c.message ?? '') }))
      : [];
  } catch {
    return [];   // no git extension, no repository: the picker still takes a typed revision
  }
}

/**
 * "Review a commit…": one picked from the recent ones — or a branch, a range typed in the picker — reviewed by
 * `/check` through the chat, like typing it.
 * ⚠ Palette only: the Source Control graph's commit menu (`scm/historyItem/context`) is a proposed API, which an
 * extension published on the Marketplace cannot contribute to.
 */
export async function reviewCommit(run: (text: string) => Promise<void>): Promise<void> {
  const commits = await recentCommits();
  type Entry = vscode.QuickPickItem & { revision: string };
  const pick = vscode.window.createQuickPick<Entry>();
  pick.placeholder = t('Pick a commit, or type a branch or A..B, to review');
  pick.matchOnDescription = true;   // a commit is found by its message, not only by its hash
  const listed: Entry[] = commits.map((c) => ({
    label: c.hash.slice(0, 8),
    description: c.message.split('\n')[0],
    revision: c.hash,
  }));
  pick.items = listed;
  pick.onDidChangeValue((value) => {
    const typed = typedRevision(value);
    // ⚠ The picker sorts what matches by where the match starts in the label: the typed entry's label begins with
    // the text, so it comes first; a hash that begins with it too names the same commit.
    pick.items = typed
      ? [{ label: typed, description: `/check ${typed} — ${t('Review this branch, tag or range')}`, revision: typed,
           alwaysShow: true }, ...listed]
      : listed;
  });
  const chosen = await new Promise<string | undefined>((resolve) => {
    pick.onDidAccept(() => {
      resolve(pick.selectedItems[0]?.revision);
      pick.hide();
    });
    pick.onDidHide(() => resolve(undefined));
    pick.show();
  });
  pick.dispose();
  if (chosen) {
    await run(`/check ${chosen}`);
  }
}
