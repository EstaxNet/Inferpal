import * as vscode from 'vscode';

/** The part of the built-in Git extension's API (`vscode.git`, version 1) the commit gesture uses. */
export interface GitRepository {
  readonly rootUri: vscode.Uri;
  readonly inputBox: { value: string };
}

interface GitApi {
  readonly repositories: GitRepository[];
  getRepository(uri: vscode.Uri): GitRepository | null;
}

interface GitExtension {
  readonly enabled: boolean;
  getAPI(version: 1): GitApi;
}

/** The Git extension's API, or `undefined` when it is disabled (the extension, or `git.enabled`). */
export async function gitApi(): Promise<GitApi | undefined> {
  const extension = vscode.extensions.getExtension<GitExtension>('vscode.git');
  if (!extension) {
    return undefined;
  }
  const git = extension.isActive ? extension.exports : await extension.activate();
  return git.enabled ? git.getAPI(1) : undefined;
}

/**
 * The repository a gesture acts on: the one the Source Control view passed, else the active file's, else the only one
 * open — several and no way to choose is `'several'`, one passed that the Git extension no longer has is `'unknown'`:
 * both said rather than guessed.
 */
export function repositoryFor(api: GitApi, rootUri?: vscode.Uri): GitRepository | 'several' | 'unknown' | undefined {
  if (rootUri) {
    return api.getRepository(rootUri) ?? 'unknown';
  }
  const active = vscode.window.activeTextEditor?.document.uri;
  const ofActive = active?.scheme === 'file' ? api.getRepository(active) : null;
  if (ofActive) {
    return ofActive;
  }
  return api.repositories.length > 1 ? 'several' : api.repositories[0];
}
