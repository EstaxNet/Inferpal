// Extension entry point: resolves the Inferpal.Host binary, spawns/supervises it,
// wires the editor bridge (reverse RPC + document sync) and registers the chat view.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { ChatViewProvider } from './chatViewProvider';
import { SettingsPanel } from './settingsPanel';
import { DebugBridge } from './debugBridge';
import { EditorBridge, panelDiagnostics } from './editorBridge';
import { HostClient } from './hostClient';
import { hostErrorText, otherWorkspaceFolders, promptOpenFolder, workspaceRoot } from './hostStatus';
import { FimProvider } from './inlineCompletions';
import { FixProblemsProvider, problemEntry } from './problemsFix';
import { reviewCommit } from './reviewCommit';
import { followModelRouterSettings, initModelRouterSync, syncModelRouterSettings } from './modelRouterSettings';
import { docsUrl } from './docsLinks';
import { setLanguage, t } from './i18n';
import { oneTest } from './testGestures';

let host: HostClient | undefined;
let bridge: EditorBridge | undefined;
let debugBridge: DebugBridge | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  initModelRouterSync(context.workspaceState);
  const output = vscode.window.createOutputChannel('Inferpal');
  context.subscriptions.push(output);
  const log = (line: string) => output.appendLine(line);

  bridge = new EditorBridge(context.secrets);
  context.subscriptions.push(bridge);

  // This is what makes debug_control / debug_inspect exist for the model on this
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
  bridge.setApprovalCard((note, token) => chatView.requestApproval(note.message, token, note.card ?? null));
  bridge.setInputCard((card, token) => chatView.requestInput(card, token));
  context.subscriptions.push(
    chatView,
    vscode.window.registerWebviewViewProvider(ChatViewProvider.viewId, chatView, {
      webviewOptions: { retainContextWhenHidden: true },
    }),
    vscode.languages.registerInlineCompletionItemProvider(
      { pattern: '**' },
      new FimProvider(() => host, log),
    ),
    // Files only: the fix reads the code around each diagnostic from the file, and edits it there.
    vscode.languages.registerCodeActionsProvider({ scheme: 'file' }, new FixProblemsProvider(), FixProblemsProvider.metadata),
    vscode.commands.registerCommand('inferpal.fixProblems', (uri?: vscode.Uri, diagnostics?: vscode.Diagnostic[]) => {
      // Run from a key or another extension, without the quick fix's arguments: the problems at the cursor.
      if (!uri || !Array.isArray(diagnostics)) {
        const editor = vscode.window.activeTextEditor;
        uri = editor?.document.uri;
        const file = panelDiagnostics().find(([u]) => u.toString() === editor?.document.uri.toString());
        diagnostics = editor && file ? file[1].filter((d) => d.range.contains(editor.selection.active)) : [];
      }
      if (!uri || diagnostics.length === 0) {
        void vscode.window.showInformationMessage(t('No problem at the cursor.'));
        return;
      }
      const at = uri;
      return chatView.fixProblems(diagnostics.map((d) => problemEntry(at, d)));
    }),
    // The call stack's and the variables' menus, and the palette: the whole stop — which line was chosen is not read.
    vscode.commands.registerCommand('inferpal.analyzeStop', () => chatView.analyzeStop()),
    // The file explorer's Inferpal submenu: the item clicked.
    vscode.commands.registerCommand('inferpal.explainItem', (uri?: vscode.Uri) => uri && chatView.explainItem(uri)),
    vscode.commands.registerCommand('inferpal.reviewItem', (uri?: vscode.Uri) => uri && chatView.reviewItem(uri)),
    vscode.commands.registerCommand('inferpal.attachItem', (uri?: vscode.Uri) => uri && chatView.attachItem(uri)),
    // Source Control passes the repository (scm/title, scm/sourceControl); the palette passes nothing.
    vscode.commands.registerCommand('inferpal.proposeCommitMessage',
      (sourceControl?: { rootUri?: vscode.Uri }) => chatView.proposeCommitMessage(sourceControl)),
    vscode.commands.registerCommand('inferpal.fixTest', (...items: vscode.TestItem[]) => {
      const item = oneTest(items);
      return item && chatView.fixTest(item);
    }),
    // The Test view passes the selected tests (testing/item/context), or a failure message with its test
    // (testing/message/context).
    vscode.commands.registerCommand('inferpal.explainTestFailure',
      (arg?: vscode.TestItem | { test?: vscode.TestItem; message: vscode.TestMessage }, ...others: vscode.TestItem[]) => {
        if (arg && 'message' in arg) {
          return arg.test && chatView.explainTestFailure(arg.test, arg.message);
        }
        const item = oneTest([arg, ...others]);
        return item && chatView.explainTestFailure(item);
      }),
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
        () => void followModelRouterSettings(host, log).then(() => chatView.configSaved()),
        () => chatView.relocalize(),
        () => void chatView.revealXray(),
        log,
      )),
    // The "?" beside the gear: this version's documentation, in the browser; not opened, its address is said.
    vscode.commands.registerCommand('inferpal.openDocs', async () => {
      const docs = docsUrl(context.extension.packageJSON.version as string | undefined);
      if (!(await vscode.env.openExternal(vscode.Uri.parse(docs)))) {
        void vscode.window.showInformationMessage(t('Full documentation (in English): {0}', docs));
      }
    }),
    // Editor context menu → same pipeline as typing the slash command in the chat.
    vscode.commands.registerCommand('inferpal.fixSelection', () => chatView.runSlashCommand('/fix')),
    vscode.commands.registerCommand('inferpal.refactorSelection', () => chatView.runSlashCommand('/refactor')),
    vscode.commands.registerCommand('inferpal.docSelection', () => chatView.runSlashCommand('/doc')),
    vscode.commands.registerCommand('inferpal.explainSelection', () => chatView.runSlashCommand('/explain')),
    vscode.commands.registerCommand('inferpal.testSelection', () => chatView.runSlashCommand('/test')),
    vscode.commands.registerCommand('inferpal.map', () => chatView.runSlashCommand('/map')),
    // Edit with AI: an instruction, then the edit previewed — outside the chat, as in Visual Studio.
    vscode.commands.registerCommand('inferpal.editSelection', () => chatView.editWithAi()),
    // A commit picked, or a branch or range typed: reviewed by /check, like typing it.
    vscode.commands.registerCommand('inferpal.reviewCommit', () =>
      reviewCommit((text) => chatView.runSlashCommand(text))),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration('inferpal.utilityModel') || e.affectsConfiguration('inferpal.modelRouterAuto')) {
        // config/update is refused while a turn runs: the push waits for it to end.
        chatView.runWhenIdle('modelRouter', () => pushModelRouterSettings(log));
      }
      if (e.affectsConfiguration('inferpal.model')) {
        void chatView.followModelSetting();
      }
    }),
    // A folder opened (or changed) after startup: (re)start the host against the new root — only when
    // that root changed. Adding a second folder restarted it anyway, killing the turn in flight, its
    // shells and MCP servers.
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      if (host?.isRunning && host.rootDir === workspaceRoot()) {
        // Same root, other folders changed: the host still states which ones its tools cannot reach.
        host.setWorkspaceFolders(otherWorkspaceFolders()).catch((err) =>
          log(`[inferpal] workspace/folders failed: ${hostErrorText(err)}`));
        return;
      }
      void startHost(context, chatView, log, false);
    }),
  );

  await startHost(context, chatView, log, false);
}

/** The host's notice for the work it is running, or undefined — never waited on for more than two seconds: a host
 *  that does not answer is being stopped anyway. */
async function stoppedWorkNotice(client: HostClient): Promise<string | undefined> {
  if (!client.isRunning) {
    return undefined;
  }
  try {
    const result = await Promise.race([
      client.runningWork(),
      new Promise<null>((resolve) => setTimeout(() => resolve(null), 2000)),
    ]);
    return result?.notice ?? undefined;
  } catch {
    return undefined;   // the host is going away; its stop is what matters here
  }
}

/**
 * Pushes the explicitly-set Model Router settings (utility model + auto mode) into the host's
 * shared config. Read-modify-write on the full JSON: `config/update` replaces the whole config
 * object, so a partial payload would wipe the rest. When a setting was never touched in VS Code,
 * the shared config (e.g. set from VS) wins for that setting.
 */
async function pushModelRouterSettings(log: (line: string) => void): Promise<void> {
  // Decided on what changed since VS Code and the shared config last agreed (syncModelRouterSettings).
  await syncModelRouterSettings(host, log);
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
    // What runs in the host beyond a turn (a /task, a background command, a docs indexing pass) dies with it: asked
    // before the stop, said once it is done — nothing else would.
    const stopped = await stoppedWorkNotice(host);
    await host.stop();
    host = undefined;
    chatView.onHostStopped();
    chatView.sayStoppedWork(stopped);
    // The host's record of the breakpoints its agent set died with it: they go with it.
    debugBridge?.releaseAgentBreakpoints();
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

  // Until the restore is done, a question waits (chatView.onHostStarting).
  chatView.onHostStarting();
  SettingsPanel.onHostStarting();
  const client = new HostClient(
    {
      hostPath,
      rootDir,
      otherFolders: otherWorkspaceFolders(),
      locale: vscode.env.language,
      clientName: `vscode/${vscode.version}`,
      log,
      onCrash: () => {
        host = undefined;
        // The breakpoints its agent set go with the host, as on a restart: no one is left to remove them.
        debugBridge?.releaseAgentBreakpoints();
        // And what it was running, as last seen: no one is left to ask either.
        chatView.onHostCrashed();
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
    await chatView.onHostStarted();
    await pushModelRouterSettings(log);
    SettingsPanel.onHostReady();
    log(`[inferpal] host ready (${hostPath})`);
  } catch (err) {
    log(`[inferpal] host start failed: ${String(err)}`);
    await client.stop();
    chatView.onHostStartFailed();
    SettingsPanel.onHostStartFailed();
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
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Release', 'net10.0', exe),
    path.join(context.extensionUri.fsPath, '..', 'Inferpal.Host', 'bin', 'Debug', 'net10.0', exe),
  ];
  return candidates.find((c) => fs.existsSync(c));
}
