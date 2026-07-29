// Extension entry point: resolves the Inferpal.Host binary, spawns/supervises it,
// wires the editor bridge (reverse RPC + document sync) and registers the chat view.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ChatViewProvider } from './chatViewProvider';
import { EditorBridge } from './editorBridge';
import { HostClient } from './hostClient';
import { FimProvider } from './inlineCompletions';

let host: HostClient | undefined;
let bridge: EditorBridge | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel('Inferpal');
  context.subscriptions.push(output);
  const log = (line: string) => output.appendLine(line);

  bridge = new EditorBridge();
  context.subscriptions.push(bridge);

  const chatView = new ChatViewProvider(context.extensionUri, () => host, () => bridge?.activeEditor(), log);
  bridge.setApprovalCard((message) => chatView.requestApproval(message));
  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider(ChatViewProvider.viewId, chatView, {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.languages.registerInlineCompletionItemProvider(
      { pattern: '**' },
      new FimProvider(() => host, log),
    ),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('inferpal.restartHost', () => startHost(context, chatView, log, true)),
    vscode.commands.registerCommand('inferpal.resetChat', () => chatView.resetConversation()),
    vscode.commands.registerCommand('inferpal.saveSession', () => chatView.saveSessionCommand()),
    vscode.commands.registerCommand('inferpal.loadSession', () => chatView.loadSessionCommand()),
    vscode.commands.registerCommand('inferpal.deleteSession', () => chatView.deleteSessionCommand()),
    // Editor context menu → same pipeline as typing the slash command in the chat.
    vscode.commands.registerCommand('inferpal.fixSelection', () => chatView.runSlashCommand('/fix')),
    vscode.commands.registerCommand('inferpal.refactorSelection', () => chatView.runSlashCommand('/refactor')),
    vscode.commands.registerCommand('inferpal.docSelection', () => chatView.runSlashCommand('/doc')),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('inferpal.utilityModel')) {
        void pushUtilityModel(log);
      }
    }),
  );

  await startHost(context, chatView, log, false);
}

/**
 * Pushes the explicitly-set utility model (Model Router: session titles, commit messages,
 * compaction summaries) into the host's shared config. Read-modify-write on the full JSON:
 * `config/update` replaces the whole config object, so a partial payload would wipe the rest.
 * When the setting was never touched in VS Code, the shared config (e.g. set from VS) wins.
 */
async function pushUtilityModel(log: (line: string) => void): Promise<void> {
  if (!host) {
    return;
  }
  const inspected = vscode.workspace.getConfiguration('inferpal').inspect<string>('utilityModel');
  const value = inspected?.workspaceValue ?? inspected?.globalValue;
  if (value === undefined) {
    return;
  }
  try {
    const cfg = JSON.parse(await host.configGet()) as { utilityModel?: string };
    if (cfg.utilityModel === value) {
      return;
    }
    cfg.utilityModel = value;
    await host.configUpdate(JSON.stringify(cfg));
    log(`[inferpal] utility model → "${value || '(chat model)'}"`);
  } catch (err) {
    log(`[inferpal] utility model sync failed: ${String(err)}`);
  }
}

export async function deactivate(): Promise<void> {
  await host?.stop();
  host = undefined;
}

async function startHost(
  context: vscode.ExtensionContext,
  chatView: ChatViewProvider,
  log: (line: string) => void,
  interactive: boolean,
): Promise<void> {
  if (host) {
    await host.stop();
    host = undefined;
  }

  const rootDir = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!rootDir) {
    log('[inferpal] no workspace folder open — host not started');
    if (interactive) {
      void vscode.window.showWarningMessage(vscode.l10n.t('Inferpal needs an open folder to start.'));
    }
    return;
  }

  const hostPath = resolveHostPath(context, rootDir);
  if (!hostPath) {
    log('[inferpal] Inferpal.Host binary not found — set "inferpal.hostPath"');
    if (interactive) {
      void vscode.window.showErrorMessage(
        vscode.l10n.t('Inferpal.Host binary not found. Set "inferpal.hostPath" in settings.'),
      );
    }
    return;
  }

  const client = new HostClient(
    {
      hostPath,
      rootDir,
      locale: vscode.env.language,
      clientName: `vscode/${vscode.version}`,
      log,
      onCrash: () => {
        host = undefined;
        void vscode.window
          .showErrorMessage(vscode.l10n.t('Inferpal host stopped unexpectedly.'), vscode.l10n.t('Restart'))
          .then((choice) => {
            if (choice) {
              void vscode.commands.executeCommand('inferpal.restartHost');
            }
          });
      },
    },
    bridge!,
  );

  try {
    await client.start();
    host = client;
    bridge!.attach(client);
    await chatView.onHostReady();
    await pushUtilityModel(log);
    log(`[inferpal] host ready (${hostPath})`);
  } catch (err) {
    log(`[inferpal] host start failed: ${String(err)}`);
    await client.stop();
    if (interactive) {
      void vscode.window.showErrorMessage(vscode.l10n.t('Inferpal host failed to start: {0}', String(err)));
    }
  }
}

/**
 * Host binary lookup order: explicit setting → host bundled with the extension
 * (Phase 5 packaging) → a dev build under the workspace (this repo's layout).
 */
function resolveHostPath(context: vscode.ExtensionContext, rootDir: string): string | undefined {
  const exe = process.platform === 'win32' ? 'Inferpal.Host.exe' : 'Inferpal.Host';

  const configured = vscode.workspace.getConfiguration('inferpal').get<string>('hostPath', '').trim();
  if (configured) {
    return fs.existsSync(configured) ? configured : undefined;
  }

  const candidates = [
    path.join(context.extensionUri.fsPath, 'host', exe),
    // F5 dev host: the extension runs from the repo's vscode/ source folder, so the
    // freshly built host lives one level up — works whatever workspace is opened.
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Release', 'net8.0', exe),
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Debug', 'net8.0', exe),
    path.join(rootDir, 'Inferpal.Host', 'bin', 'Release', 'net8.0', exe),
    path.join(rootDir, 'Inferpal.Host', 'bin', 'Debug', 'net8.0', exe),
  ];
  return candidates.find((c) => fs.existsSync(c));
}
