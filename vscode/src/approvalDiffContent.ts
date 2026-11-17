import * as vscode from 'vscode';

/**
 * The two sides of an approval card's files, served read-only under a scheme of their own so VS Code's diff editor shows
 * the change before it is allowed: "Open diff" on a card that changes a file.
 */
export class ApprovalDiffContent implements vscode.TextDocumentContentProvider {
  static readonly scheme = 'inferpal-approval';

  /** By "before|after/<card id>/<encoded path>". Bounded: the oldest go first. */
  static readonly texts = new Map<string, string>();

  /** Keeps a side's text for its URI. */
  static put(key: string, text: string): void {
    ApprovalDiffContent.texts.set(key, text);
    while (ApprovalDiffContent.texts.size > 200) {
      ApprovalDiffContent.texts.delete(ApprovalDiffContent.texts.keys().next().value!);
    }
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    // /before/<id>/<encoded path>/<file name>: the file name only names the tab.
    const key = uri.path.split('/').slice(1, 4).join('/');
    return ApprovalDiffContent.texts.get(key) ?? '';
  }
}
