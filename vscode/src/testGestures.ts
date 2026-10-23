import * as vscode from 'vscode';
import { t } from './i18n';

/**
 * The test a Test view gesture acts on. Several selected: the view passes them all, and the gesture takes one test at a
 * time — the one it takes is named, never the others dropped without a word.
 */
export function oneTest(items: (vscode.TestItem | undefined)[]): vscode.TestItem | undefined {
  const [first, ...others] = items;
  if (first && others.length > 0) {
    void vscode.window.showInformationMessage(t('One test at a time: {0} is the one used.', first.label));
  }
  return first;
}

/**
 * The `run_tests` / `/tdd` filter of a test of the Test view: its name without its parameters — `Add(1, 2)` is a theory's
 * case, and a parenthesis is syntax in a dotnet filter expression.
 */
export function testFilter(item: vscode.TestItem): string {
  const paren = item.label.indexOf('(');
  return (paren > 0 ? item.label.slice(0, paren) : item.label).trim();
}

/** A failure message as the Test view shows it: its text, then what was expected and what came. */
export function failureText(message: vscode.TestMessage): string {
  const text = typeof message.message === 'string' ? message.message : message.message.value;
  const parts = [text];
  if (message.expectedOutput !== undefined) {
    parts.push(`Expected:\n${message.expectedOutput}`);
  }
  if (message.actualOutput !== undefined) {
    parts.push(`Actual:\n${message.actualOutput}`);
  }
  return parts.join('\n\n');
}
