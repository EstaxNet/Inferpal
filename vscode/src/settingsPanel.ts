// "Inferpal Settings" panel: a singleton WebviewPanel mirroring the VS settings window
// (the seven pages of the Core schema, side navigation). The form is rendered by
// src/webview/settings.ts from the host's config JSON (config/get); Save round-trips the
// FULL JSON through config/update (absent fields would reset — the webview mutates the
// parsed original object, never rebuilds it). What the form asks is answered by the shared
// bridge (settingsBridge.ts) — the same one the browser settings window uses; this file is
// VS Code's shell around it.
import * as vscode from 'vscode';
import * as crypto from 'crypto';
import { HostClient } from './hostClient';
import { hostErrorText, hostUnavailableMessage, promptOpenFolder } from './hostStatus';
import { SavedKeys, SettingsBridge, SettingsInbound } from './settingsBridge';
import { setLanguage, t } from './i18n';

export class SettingsPanel {
  private static current: SettingsPanel | undefined;
  /** Set by the activator from the host's spawn until its start ends: the panel then says "starting", not "restart". */
  private static hostStarting = false;

  private readonly bridge: SettingsBridge;

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly extensionUri: vscode.Uri,
    private readonly getHost: () => HostClient | undefined,
    private readonly onSaved: () => void,
    private readonly onLanguageChanged: () => void,
    private readonly openXray: () => void,
    private readonly log: (line: string) => void,
    // Asked once: the panel renders again on a language change, and must not jump back there.
    private initialPage: { page: string; section?: string } | undefined,
  ) {
    this.bridge = new SettingsBridge({
      host: () => this.getHost(),
      post: (message) => this.post(message),
      log: (line) => this.log(line),
      hostStarting: () => SettingsPanel.hostStarting,
      startingMessage: () => t('Inferpal is starting — the settings load as soon as it is ready.'),
      unavailableMessage: () => hostUnavailableMessage(),
      onHostMissing: () => promptOpenFolder(),
      errorText: (err) => hostErrorText(err),
      openFile: (path) => SettingsPanel.openFile(path),
      browseFile: async () => {
        const picked = await vscode.window.showOpenDialog({ canSelectMany: false, canSelectFiles: true, canSelectFolders: false });
        return picked?.[0]?.fsPath;
      },
      openXray: () => this.openXray(),
      saved: (before, after) => this.afterSave(before, after),
    });
    panel.webview.html = this.renderHtml(panel.webview);
    panel.webview.onDidReceiveMessage((msg: SettingsInbound) => void this.bridge.onMessage(msg));
    panel.onDidDispose(() => {
      if (SettingsPanel.current === this) {
        SettingsPanel.current = undefined;
      }
    });
  }

  static open(
    extensionUri: vscode.Uri,
    getHost: () => HostClient | undefined,
    onSaved: () => void,
    onLanguageChanged: () => void,
    openXray: () => void,
    log: (line: string) => void,
    page?: string,
    section?: string,
  ): void {
    if (SettingsPanel.current) {
      SettingsPanel.current.panel.reveal();
      if (page) {
        SettingsPanel.current.post({ type: 'showPage', page, section });
      }
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      'inferpal.settings',
      t('Inferpal Settings'),
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')],
        // The form writes only on Save: a hidden editor tab's webview is destroyed otherwise, and
        // coming back reloads it from the config — unsaved edits gone, without a word.
        retainContextWhenHidden: true,
      },
    );
    SettingsPanel.current = new SettingsPanel(panel, extensionUri, getHost, onSaved, onLanguageChanged, openXray, log,
      page ? { page, section } : undefined);
  }

  /** Called by the activator just before it spawns the host. */
  static onHostStarting(): void {
    SettingsPanel.hostStarting = true;
  }

  /**
   * The host is up: a panel opened while it was starting, stopped or missing its folder loads its form now. Without
   * this it stayed on "Loading settings…" under a remedy that no longer applied, until closed and reopened.
   */
  static onHostReady(): void {
    SettingsPanel.hostStarting = false;
    SettingsPanel.current?.bridge.loadIfNotLoaded();
  }

  /** A command in the chat saved the settings (`/tools off`, `/import … apply`): the open panel's live blocks read again. */
  static onSavedElsewhere(): void {
    SettingsPanel.current?.post({ type: 'savedElsewhere' });
  }

  /** The start failed: a panel waiting on "starting" says what applies now. */
  static onHostStartFailed(): void {
    SettingsPanel.hostStarting = false;
    SettingsPanel.current?.bridge.loadIfNotLoaded();
  }

  /** A project file opens in the editor; a folder (the rules) is shown in the explorer. */
  private static async openFile(path: string): Promise<void> {
    const uri = vscode.Uri.file(path);
    const stat = await vscode.workspace.fs.stat(uri);
    if (stat.type & vscode.FileType.Directory) {
      await vscode.commands.executeCommand('revealInExplorer', uri);
    } else {
      await vscode.window.showTextDocument(uri, { preview: false });
    }
  }

  private async afterSave(before: SavedKeys | null, after: SavedKeys | null): Promise<void> {
    this.onSaved();

    // A new language takes effect at once, as in Visual Studio: this panel and the chat re-render with it (the
    // host already switched). The labels fetched when the panel loaded would otherwise stay in the old one.
    if (after && setLanguage(after.language, this.extensionUri)) {
      this.panel.title = t('Inferpal Settings');
      this.panel.webview.html = this.renderHtml(this.panel.webview);
      this.onLanguageChanged();
    }

    // Provider/BaseUrl changes only take effect after a new `initialize`.
    if (before && after && (before.provider !== after.provider || before.baseUrl !== after.baseUrl)) {
      const restart = await vscode.window.showInformationMessage(
        t('Provider or server URL changed — restart the Inferpal host to apply it.'),
        t('Restart'),
      );
      if (restart) {
        void vscode.commands.executeCommand('inferpal.restartHost');
      }
    }
  }

  private post(message: unknown): void {
    void this.panel.webview.postMessage(message);
  }

  /** Adapter-side strings injected into the settings webview (window.__l10n). The field
   * labels/hints/sections come from the host instead (`settings/strings`, same .resx as VS). */
  private static strings(): Record<string, string> {
    return {
      'Settings saved.': t('Settings saved.'),
      'Loading settings…': t('Loading settings…'),
      'Connected': t('Connected'),
      'Backend unreachable': t('Backend unreachable'),
      'Refresh models': t('Refresh models'),
      'Show all models': t('Show all models'),
      'No model listed — is the backend reachable?': t('No model listed — is the backend reachable?'),
      'The server lists no chat model — only models that cannot answer (embedding, reranking). Install a chat model.':
        t('The server lists no chat model — only models that cannot answer (embedding, reranking). Install a chat model.'),
    };
  }

  private renderHtml(webview: vscode.Webview): string {
    const script = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'settings.js'));
    const style = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'settings.css'));
    const nonce = crypto.randomBytes(16).toString('base64');
    const l10n = JSON.stringify(SettingsPanel.strings()).replace(/</g, '\\u003c');
    // A page asked for by name: read before the bundle, so the form renders on it rather than after a message.
    const page = JSON.stringify(this.initialPage ?? null).replace(/</g, '\\u003c');
    this.initialPage = undefined;
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource};">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>Inferpal Settings</title>
</head>
<body>
<div id="app"></div>
<script nonce="${nonce}">window.__l10n = ${l10n}; window.__settingsPage = ${page};</script>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }
}
