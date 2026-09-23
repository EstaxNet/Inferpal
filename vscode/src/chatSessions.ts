// Session plumbing for the chat view: the transcript ⇄ saved-message mapping, the session
// QuickPick (which doubles as the branch navigator) and the export rendering. All of it is either
// pure or depends only on the host client — none of it needs the provider's mutable state.
import * as vscode from 'vscode';
import { t } from './i18n';
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
  const { sessions, notice } = await host.sessionList();
  // ⚠ A file the store could not read is not in `sessions`. Unsaid, "No saved sessions." is a
  // claim about the user's own data made about files the product failed to open.
  if (notice) {
    void vscode.window.showWarningMessage(notice);
  }
  if (sessions.length === 0) {
    if (!notice) {
      void vscode.window.showInformationMessage(t('No saved sessions.'));
    }
    return undefined;
  }
  const picked = await vscode.window.showQuickPick(
    sessions.map((s) => ({
      label: s.parent ? `$(git-branch) ${s.name}` : s.name,
      description: s.parent
        ? t('{0} msg · from {1} @ turn {2}', s.messageCount, s.parent, s.forkTurn ?? 0)
        : t('{0} msg', s.messageCount),
      detail: s.preview,
      name: s.name,
    })),
    { placeHolder: placeholder },
  );
  return picked?.name;
}
