import * as vscode from 'vscode';
import { t } from './i18n';
import type { ProblemEntry } from './protocol';

/**
 * "Fix with Inferpal" among the quick fixes of every diagnostic — the editor's light bulb and the Problems panel, the
 * doors Visual Studio's Error List menu has there. The command asks the chat with the diagnostics the action holds.
 */
export class FixProblemsProvider implements vscode.CodeActionProvider {
  static readonly metadata: vscode.CodeActionProviderMetadata = {
    providedCodeActionKinds: [vscode.CodeActionKind.QuickFix],
  };

  provideCodeActions(
    document: vscode.TextDocument,
    _range: vscode.Range | vscode.Selection,
    context: vscode.CodeActionContext,
  ): vscode.CodeAction[] {
    // Outside the workspace the agent's tools write nothing: offered there, the fix could only be refused.
    if (context.diagnostics.length === 0 || !vscode.workspace.getWorkspaceFolder(document.uri)) {
      return [];
    }
    const action = new vscode.CodeAction(t('Fix with Inferpal'), vscode.CodeActionKind.QuickFix);
    action.diagnostics = [...context.diagnostics];
    action.command = {
      command: 'inferpal.fixProblems',
      title: action.title,
      arguments: [document.uri, [...context.diagnostics]],
    };
    return [action];
  }
}

/** A diagnostic as the Core reads it: 1-based positions, the severity in a build's words. */
export function problemEntry(uri: vscode.Uri, d: vscode.Diagnostic): ProblemEntry {
  const code = typeof d.code === 'object' ? d.code.value : d.code;
  return {
    file: uri.scheme === 'file' ? uri.fsPath : null,
    line: d.range.start.line + 1,
    column: d.range.start.character + 1,
    severity:
      d.severity === vscode.DiagnosticSeverity.Error ? 'error' : d.severity === vscode.DiagnosticSeverity.Warning ? 'warning' : 'info',
    code: code === undefined ? null : String(code),
    text: d.message,
    source: d.source ?? null,
  };
}
