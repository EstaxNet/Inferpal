// Extension entry point: resolves the Inferpal.Host binary, spawns/supervises it,
// wires the editor bridge (reverse RPC + document sync) and registers the chat view.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ChatViewProvider } from './chatViewProvider';
import { SettingsPanel } from './settingsPanel';
import { DebugBridge } from './debugBridge';
import { EditorBridge } from './editorBridge';
import { HostClient } from './hostClient';
import { hostErrorText, promptOpenFolder, workspaceRoot } from './hostStatus';
import { FimProvider } from './inlineCompletions';
import { setLanguage, t } from './i18n';

let host: HostClient | undefined;
let bridge: EditorBridge | undefined;
let debugBridge: DebugBridge | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const output = vscode.window.createOutputChannel('Inferpal');
  context.subscriptions.push(output);
  const log = (line: string) => output.appendLine(line);

  bridge = new EditorBridge(context.secrets);
  context.subscriptions.push(bridge);

  // Roadmap §21: this is what makes debug_control / debug_inspect exist for the model on this
  // front-end. Created unconditionally — vscode.debug is always there — while whether *this*
  // workspace can actually launch anything is answered at start time, in words the agent can use.
  debugBridge = new DebugBridge(log);
  context.subscriptions.push(debugBridge);

  const chatView = new ChatViewProvider(
    context,
    () => host,
    () => bridge?.activeEditor(),
    () => bridge?.editorDiagnostics() ?? Promise.resolve(null),
    log,
  );
  bridge.setApprovalCard((message, token) => chatView.requestApproval(message, token));
  context.subscriptions.push(
    chatView,
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
    vscode.commands.registerCommand('inferpal.exportChat', () => chatView.exportCommand()),
    vscode.commands.registerCommand('inferpal.openSettings', () =>
      SettingsPanel.open(
        context.extensionUri,
        () => host,
        () => void followPanelModelRouterSettings(log).then(() => chatView.configSaved()),
        () => chatView.relocalize(),
        log,
      )),
    // Editor context menu → same pipeline as typing the slash command in the chat.
    vscode.commands.registerCommand('inferpal.fixSelection', () => chatView.runSlashCommand('/fix')),
    vscode.commands.registerCommand('inferpal.refactorSelection', () => chatView.runSlashCommand('/refactor')),
    vscode.commands.registerCommand('inferpal.docSelection', () => chatView.runSlashCommand('/doc')),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('inferpal.utilityModel') || e.affectsConfiguration('inferpal.modelRouterAuto')) {
        // config/update is refused while a turn runs: the push waits for it to end.
        chatView.runWhenIdle('modelRouter', () => pushModelRouterSettings(log));
      }
    }),
    // A folder opened (or changed) after startup: (re)start the host against the new root — only when
    // that root changed. Adding a second folder restarted it anyway, killing the turn in flight, its
    // shells and MCP servers.
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      if (host?.isRunning && host.rootDir === workspaceRoot()) {
        return;
      }
      void startHost(context, chatView, log, false);
    }),
  );

  await startHost(context, chatView, log, false);
}

/**
 * Pushes the explicitly-set Model Router settings (utility model + auto mode) into the host's
 * shared config. Read-modify-write on the full JSON: `config/update` replaces the whole config
 * object, so a partial payload would wipe the rest. When a setting was never touched in VS Code,
 * the shared config (e.g. set from VS) wins for that setting.
 */
async function pushModelRouterSettings(log: (line: string) => void): Promise<void> {
  if (!host) {
    return;
  }
  const config = vscode.workspace.getConfiguration('inferpal');
  const utilityInspected = config.inspect<string>('utilityModel');
  const utility = utilityInspected?.workspaceValue ?? utilityInspected?.globalValue;
  const autoInspected = config.inspect<boolean>('modelRouterAuto');
  const auto = autoInspected?.workspaceValue ?? autoInspected?.globalValue;
  if (utility === undefined && auto === undefined) {
    return;
  }
  try {
    const cfg = JSON.parse(await host.configGet()) as { utilityModel?: string; modelRouterAuto?: boolean };
    let changed = false;
    if (utility !== undefined && cfg.utilityModel !== utility) {
      cfg.utilityModel = utility;
      changed = true;
      log(`[inferpal] utility model → "${utility || '(chat model)'}"`);
    }
    if (auto !== undefined && cfg.modelRouterAuto !== auto) {
      cfg.modelRouterAuto = auto;
      changed = true;
      log(`[inferpal] model router auto → ${auto}`);
    }
    if (changed) {
      await host.configUpdate(JSON.stringify(cfg));
    }
  } catch (err) {
    log(`[inferpal] model router settings sync failed: ${String(err)}`);
  }
}

/** The scope whose value wins for a setting (workspace over global), or undefined when neither is set. */
function explicitTarget<T>(inspected: { workspaceValue?: T; globalValue?: T } | undefined): vscode.ConfigurationTarget | undefined {
  if (inspected?.workspaceValue !== undefined) {
    return vscode.ConfigurationTarget.Workspace;
  }
  return inspected?.globalValue !== undefined ? vscode.ConfigurationTarget.Global : undefined;
}

/**
 * The reverse of pushModelRouterSettings, after a save in the settings panel. The explicitly-set VS Code
 * values are pushed into the shared config at every host start, so a utility model or auto mode changed in
 * the panel was silently reverted the next time. The panel's value is written back into the explicit
 * setting, at the scope that wins; a setting never touched in VS Code is left alone — the shared config
 * already wins for it.
 */
async function followPanelModelRouterSettings(log: (line: string) => void): Promise<void> {
  if (!host) {
    return;
  }
  const config = vscode.workspace.getConfiguration('inferpal');
  try {
    const cfg = JSON.parse(await host.configGet()) as { utilityModel?: string; modelRouterAuto?: boolean };
    const utility = config.inspect<string>('utilityModel');
    const utilityTarget = explicitTarget(utility);
    if (utilityTarget !== undefined && cfg.utilityModel !== undefined
        && (utility?.workspaceValue ?? utility?.globalValue) !== cfg.utilityModel) {
      await config.update('utilityModel', cfg.utilityModel, utilityTarget);
    }
    const auto = config.inspect<boolean>('modelRouterAuto');
    const autoTarget = explicitTarget(auto);
    if (autoTarget !== undefined && cfg.modelRouterAuto !== undefined
        && (auto?.workspaceValue ?? auto?.globalValue) !== cfg.modelRouterAuto) {
      await config.update('modelRouterAuto', cfg.modelRouterAuto, autoTarget);
    }
  } catch (err) {
    log(`[inferpal] model router settings follow failed: ${String(err)}`);
  }
}

export async function deactivate(): Promise<void> {
  await host?.stop();
  host = undefined;
}

// Serializes startHost: the command `inferpal.restartHost` and onDidChangeWorkspaceFolders can
// overlap, and two interleaved starts each saw `host === undefined` after the first await and
// spawned two .NET processes — the loser orphaned with its MCP servers and shells, and its
// onCrash later clobbered the winner.
let startChain: Promise<void> = Promise.resolve();

function startHost(
  context: vscode.ExtensionContext,
  chatView: ChatViewProvider,
  log: (line: string) => void,
  interactive: boolean,
): Promise<void> {
  // .catch first: one failed start must not leave the chain rejected and every later start dead.
  startChain = startChain.catch(() => undefined).then(() => startHostCore(context, chatView, log, interactive));
  return startChain;
}

async function startHostCore(
  context: vscode.ExtensionContext,
  chatView: ChatViewProvider,
  log: (line: string) => void,
  interactive: boolean,
): Promise<void> {
  if (host) {
    await host.stop();
    host = undefined;
    chatView.onHostStopped();
  }

  const rootDir = workspaceRoot();
  if (!rootDir) {
    log('[inferpal] no workspace folder open — host not started');
    // Always tell the user (not only on manual restart): the empty-looking chat and the
    // unreachable settings panel are baffling without this hint. The chat and the settings
    // panel say the same thing when they are the ones being clicked (hostStatus.ts) — this
    // toast can be dismissed, and it was, which is how a fresh install got told to restart
    // a host that was never meant to be running.
    promptOpenFolder();
    return;
  }

  const hostPath = resolveHostPath(context);
  if (!hostPath) {
    log('[inferpal] Inferpal.Host binary not found — set "inferpal.hostPath"');
    if (interactive) {
      void vscode.window.showErrorMessage(
        t('Inferpal.Host binary not found. Set "inferpal.hostPath" in settings.'),
      );
    }
    return;
  }

  // The VSIX is a zip built on a Windows CI runner: the bundled host's exec bit does not
  // survive packaging, so a fresh install on Linux/macOS would spawn straight into EACCES.
  // Re-asserting the bit is idempotent; a failure here surfaces as the spawn error right below.
  if (process.platform !== 'win32') {
    try {
      fs.chmodSync(hostPath, 0o755);
    } catch {
      // Best-effort — e.g. a read-only hostPath pointed at by the user setting.
    }
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
        // vscode-jsonrpc does not cancel the host's pending requests to us: without this, an
        // approval card of the dead host stays clickable, and hydrate re-posts it on every reveal.
        chatView.onHostStopped();
        void vscode.window
          .showErrorMessage(t('Inferpal host stopped unexpectedly.'), t('Restart'))
          .then((choice) => {
            if (choice) {
              void vscode.commands.executeCommand('inferpal.restartHost');
            }
          });
      },
    },
    bridge!,
    debugBridge,
  );

  try {
    await client.start();
    host = client;
    bridge!.attach(client);
    // The language picked in Inferpal's settings, not VS Code's alone — applied before the chat hydrates.
    try {
      const saved = JSON.parse(await client.configGet()) as { language?: string };
      if (setLanguage(saved.language, context.extensionUri)) {
        chatView.relocalize();
      }
    } catch (err) {
      log(`[inferpal] language sync failed: ${String(err)}`);
    }
    await chatView.onHostReady();
    await pushModelRouterSettings(log);
    log(`[inferpal] host ready (${hostPath})`);
  } catch (err) {
    log(`[inferpal] host start failed: ${String(err)}`);
    await client.stop();
    if (interactive) {
      void vscode.window.showErrorMessage(t('Inferpal host failed to start: {0}', hostErrorText(err)));
    }
  }
}

/**
 * Host binary lookup order: explicit (machine-scoped) setting → host bundled with the
 * extension (Phase 5 packaging) → a dev build next to the extension source (F5 flow).
 * Never anything taken from the opened workspace.
 */
function resolveHostPath(context: vscode.ExtensionContext): string | undefined {
  const exe = process.platform === 'win32' ? 'Inferpal.Host.exe' : 'Inferpal.Host';

  const configured = vscode.workspace.getConfiguration('inferpal').get<string>('hostPath', '').trim();
  if (configured) {
    return fs.existsSync(configured) ? configured : undefined;
  }

  // Every candidate is relative to the *extension*, never to the opened workspace: probing
  // the workspace would mean spawning a binary shipped by whatever repository the user just
  // cloned. `inferpal.hostPath` is machine-scoped for the same reason — a repo's
  // .vscode/settings.json must not be able to choose which executable we launch.
  const candidates = [
    path.join(context.extensionUri.fsPath, 'host', exe),
    // F5 dev host: the extension runs from the repo's vscode/ source folder, so the
    // freshly built host lives one level up — works whatever workspace is opened.
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Release', 'net8.0', exe),
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Debug', 'net8.0', exe),
  ];
  return candidates.find((c) => fs.existsSync(c));
}
