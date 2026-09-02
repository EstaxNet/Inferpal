// One place decides what "the host is unavailable" means for the user, because the remedy
// differs and naming the wrong one is worse than saying nothing.
//
// With no folder open the host is deliberately never spawned (`startHostCore` returns before
// the spawn — the workspace root is a required `initialize` parameter), so "Inferpal: Restart
// Host" is inert in exactly that state. Measured on a fresh install (2026-09-02): VS Code opens
// on the Welcome tab with no folder, and the chat greeted the first message with that command —
// a first contact whose only advice cannot work. The output channel named the state correctly
// ("no workspace folder open — host not started"); the UI did not.
import * as vscode from 'vscode';

/**
 * The host's root directory, or undefined when no folder is open. Its absence *is* the
 * "not started" state — this is the single definition of that condition, shared with
 * `startHostCore` so the message and the behaviour can never disagree.
 */
export function workspaceRoot(): string | undefined {
  return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
}

/** Inline text (chat bubble, settings panel) naming the remedy that actually applies. */
export function hostUnavailableMessage(): string {
  return workspaceRoot()
    ? vscode.l10n.t('Inferpal host is not running — use "Inferpal: Restart Host".')
    : vscode.l10n.t('Inferpal needs an open folder to start — open one and it starts on its own.');
}

/**
 * Toast carrying the one-click way out. No-op when a folder is already open, so a call site
 * can pair it with {@link hostUnavailableMessage} without re-testing the state itself.
 */
export function promptOpenFolder(): void {
  if (workspaceRoot()) {
    return;
  }
  void vscode.window
    .showWarningMessage(vscode.l10n.t('Inferpal needs an open folder to start.'), vscode.l10n.t('Open Folder'))
    .then((choice) => {
      if (choice) {
        void vscode.commands.executeCommand('vscode.openFolder');
      }
    });
}
