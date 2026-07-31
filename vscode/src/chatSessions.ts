// Session plumbing for the chat view: the transcript ⇄ saved-message mapping, the session
// QuickPick (which doubles as the branch navigator) and the export rendering. All of it is either
// pure or depends only on the host client — none of it needs the provider's mutable state.
import * as vscode from 'vscode';
import { HostClient } from './hostClient';
import { SavedMessage } from './protocol';
import { WvTranscriptItem } from './webviewMessages';

/** Persistable view of a transcript (the shape the VS extension saves). */
export function toSavedMessages(transcript: readonly WvTranscriptItem[]): SavedMessage[] {
  return transcript.map((item) =>
    item.role === 'tool'
      ? { role: 'tool', content: item.toolOutput ?? '', toolName: item.text, timestamp: item.timestamp }
      : { role: item.role, content: item.text, timestamp: item.timestamp },
  );
}

/** The inverse: a restored session rendered back into transcript items. */
export function toTranscript(messages: readonly SavedMessage[]): WvTranscriptItem[] {
  const items: WvTranscriptItem[] = [];
  for (const m of messages) {
    if (m.role === 'tool') {
      items.push({
        role: 'tool',
        text: m.toolName ?? 'tool',
        toolOutput: m.content,
        timestamp: m.timestamp ?? undefined,
      });
    } else if (m.role === 'user' || m.role === 'assistant' || m.role === 'error') {
      items.push({ role: m.role, text: m.content, timestamp: m.timestamp ?? undefined });
    }
  }
  return items;
}

/**
 * Session picker. Branches created by `/branch` show their fork point, so this doubles as the
 * branch navigator. Returns the session name, or undefined when there is nothing to pick.
 */
export async function pickSession(host: HostClient, placeholder: string): Promise<string | undefined> {
  const sessions = await host.sessionList();
  if (sessions.length === 0) {
    void vscode.window.showInformationMessage(vscode.l10n.t('No saved sessions.'));
    return undefined;
  }
  const picked = await vscode.window.showQuickPick(
    sessions.map((s) => ({
      label: s.parent ? `$(git-branch) ${s.name}` : s.name,
      description: s.parent
        ? vscode.l10n.t('{0} msg · from {1} @ turn {2}', s.messageCount, s.parent, s.forkTurn ?? 0)
        : `${s.messageCount} msg`,
      detail: s.preview,
      name: s.name,
    })),
    { placeHolder: placeholder },
  );
  return picked?.name;
}

/** Markdown rendering of a conversation for `/export` and the export command. */
export function renderExport(transcript: readonly WvTranscriptItem[]): string {
  const lines: string[] = [];
  for (const item of transcript) {
    if (item.role === 'tool') {
      lines.push(`### 🔧 ${item.text}`, '', '```', item.toolOutput ?? '', '```', '');
    } else {
      const label = item.role === 'user' ? '## 🧑' : item.role === 'error' ? '## ⚠' : '## 🤖';
      lines.push(`${label}${item.timestamp ? ` _${item.timestamp}_` : ''}`, '', item.text, '');
    }
  }
  return lines.join('\n');
}
