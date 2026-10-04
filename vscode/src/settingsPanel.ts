// "Inferpal Settings" panel: a singleton WebviewPanel mirroring the VS settings window
// (the seven pages of the Core schema, side navigation). The form is rendered by
// src/webview/settings.ts from the host's config JSON (config/get); Save round-trips the
// FULL JSON through config/update (absent fields would reset — the webview mutates the
// parsed original object, never rebuilds it).
import * as vscode from 'vscode';
import * as crypto from 'crypto';
import { HostClient } from './hostClient';
import { hostErrorText, hostUnavailableMessage, promptOpenFolder } from './hostStatus';
import { SettingsSchema } from './protocol';
import { setLanguage, t } from './i18n';

interface SettingsInbound {
  type: 'ready' | 'save' | 'testConnection' | 'refreshModels'
    | 'mcpCards' | 'mcpRetry' | 'mcpAuthorize' | 'rulesTable' | 'newRule' | 'browsePinned'
    | 'indexCard' | 'indexRebuild' | 'exclusions' | 'docsSites' | 'docsAction' | 'contextUsage' | 'projectFiles'
    | 'pinSizes' | 'openFile' | 'openXray';
  json?: string;
  /** docsAction: add | reindex | remove, and the URL or the site's id. */
  verb?: 'add' | 'reindex' | 'remove';
  arg?: string;
  /** openFile: the file or folder to open. */
  path?: string;
  /** pinSizes: the pinned paths the form holds. */
  pins?: string;
  /** mcpAuthorize: the server. */
  name?: string;
  /** rulesTable: the machine rules the form holds; newRule: the rule being added. */
  rules?: string;
  allow?: boolean;
  tool?: string;
  pattern?: string;
  /** Echoed back so the webview applies only the answer to its LAST request. */
  requestId?: number;
  // ⚠ What the FORM holds, not what is saved. The webview was already sending `baseUrl` and the
  // handler never read it: Test answered about the saved URL, so it could report "Connected" about
  // a different one. A field declared and never read is worse than a missing field - it makes you
  // believe the information travels.
  baseUrl?: string;
  provider?: string;
  apiKey?: string;
}

export class SettingsPanel {
  private static current: SettingsPanel | undefined;

  private lastConfigJson = '';

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly extensionUri: vscode.Uri,
    private readonly getHost: () => HostClient | undefined,
    private readonly onSaved: () => void,
    private readonly onLanguageChanged: () => void,
    private readonly openXray: () => void,
    private readonly log: (line: string) => void,
  ) {
    panel.webview.html = this.renderHtml(panel.webview);
    panel.webview.onDidReceiveMessage((msg: SettingsInbound) => void this.onMessage(msg));
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
  ): void {
    if (SettingsPanel.current) {
      SettingsPanel.current.panel.reveal();
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
    SettingsPanel.current = new SettingsPanel(panel, extensionUri, getHost, onSaved, onLanguageChanged, openXray, log);
  }

  private async onMessage(msg: SettingsInbound): Promise<void> {
    const host = this.getHost();
    switch (msg.type) {
      case 'ready': {
        if (!host?.isRunning) {
          // Same two states as the chat bubble — an empty form telling the user to restart a
          // host that no folder allowed to start is the worst of the two wordings.
          this.post({ type: 'error', message: hostUnavailableMessage() });
          promptOpenFolder();
          return;
        }
        try {
          this.lastConfigJson = await host.configGet();
          let models: string[] = [];
          try {
            models = await host.modelsList();
          } catch {
            // ⚠ A host or RPC failure, NOT an unreachable backend: that one does not fail, it
            // returns an empty list through the success path. Both end up
            // as `models = []` here, and the popup is what names them.
          }
          // Labels/hints/sections from the host's .resx — identical wording to the VS window.
          let strings: Record<string, string> = {};
          try {
            strings = await host.settingsStrings();
          } catch (err) {
            this.log(`[settings] settings/strings failed: ${String(err)}`);
          }
          // The form itself is declared in the Core and served over RPC: adding a setting no
          // longer means editing a TypeScript table too.
          let schema: SettingsSchema | null = null;
          try {
            schema = await host.settingsSchema();
          } catch (err) {
            this.log(`[settings] settings/schema failed: ${String(err)}`);
          }
          this.post({ type: 'init', configJson: this.lastConfigJson, models, strings, schema });
        } catch (err) {
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'testConnection': {
        if (!host?.isRunning) {
          // ⚠ On its own, `ok: false` reads as "Backend unreachable" — the wrong cause when it is
          // the HOST that is gone, and it sends the user to check a server that is answering. The
          // result clears the probe, the message names what actually failed.
          this.post({ type: 'testResult', ok: false, provider: null });
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          // Probe the URL the user is LOOKING AT - the one in the form - and name the backend that
          // answered, exactly like the Visual Studio Test button (which reads BaseUrl from its own
          // form, auto-selects the detected provider, then refreshes models from that URL).
          const result = await host.connectionCheck(msg.baseUrl, msg.apiKey);
          this.post({ type: 'testResult', ok: result.ok, provider: result.provider, refused: result.refused ?? null });
        } catch (err) {
          // Same reading, one level down: an unreachable backend does not throw — it answers
          // `ok: false` through the success path. A throw here is the host, and saying "Backend
          // unreachable" about it points at the one thing that is not broken.
          this.post({ type: 'testResult', ok: false, provider: null });
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'refreshModels': {
        if (!host?.isRunning) {
          // ⚠ A bare return here is a ↻ button that does NOTHING: indistinguishable from a backend
          // serving no model. The 'ready' and 'testConnection' cases already name that state;
          // this one and 'save' did not.
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          this.post({
            type: 'models',
            models: await host.modelsList({
              baseUrl: msg.baseUrl, provider: msg.provider, apiKey: msg.apiKey,
            }),
          });
        } catch (err) {
          // ⚠ The other branch of the same ↻ button: a refresh that fails leaves the list exactly
          // as it was, which reads as "the backend serves these and no more". The log keeps the
          // cause for whoever opens the channel; the message is what the clicker gets.
          this.log(`[settings] models/list failed: ${String(err)}`);
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'mcpCards':
      case 'mcpRetry':
      case 'mcpAuthorize': {
        if (!host?.isRunning) {
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          const result = msg.type === 'mcpRetry' ? await host.mcpRetry()
            : msg.type === 'mcpAuthorize' ? await host.mcpAuthorize(msg.name ?? '')
            : await host.mcpCards();
          this.post({ type: 'mcpCards', result });
        } catch (err) {
          // The cards would otherwise keep their last state, which reads as "nothing changed".
          this.log(`[settings] ${msg.type} failed: ${String(err)}`);
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'rulesTable': {
        if (!host?.isRunning) {
          return;   // the text editor stays usable; 'ready' already said the host is gone
        }
        try {
          this.post({ type: 'rulesTable', table: await host.permissionsTable(msg.rules ?? ''), requestId: msg.requestId });
        } catch (err) {
          this.log(`[settings] permissions/table failed: ${String(err)}`);
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'newRule': {
        if (!host?.isRunning) {
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          const line = await host.permissionsNewRule(msg.allow === true, msg.tool ?? '', msg.pattern ?? '');
          this.post({ type: 'newRule', line, requestId: msg.requestId });
        } catch (err) {
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'indexCard':
      case 'indexRebuild':
      case 'exclusions':
      case 'docsSites':
      case 'docsAction':
      case 'contextUsage':
      case 'projectFiles':
      case 'pinSizes': {
        // The live blocks of the pages: each asks the host for what it shows, through the Core presenters.
        if (!host?.isRunning) {
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          switch (msg.type) {
            case 'indexCard':    this.post({ type: 'indexCard', card: await host.settingsIndexCard() }); break;
            case 'indexRebuild': this.post({ type: 'indexCard', card: await host.settingsIndexRebuild() }); break;
            case 'exclusions':   this.post({ type: 'exclusions', exclusions: await host.settingsExclusions() }); break;
            case 'docsSites':    this.post({ type: 'docsSites', docs: await host.settingsDocsSites() }); break;
            case 'docsAction':
              this.post({ type: 'docsSites', docs: await host.settingsDocsAction(msg.verb ?? 'reindex', msg.arg ?? '') });
              break;
            case 'contextUsage': this.post({ type: 'contextUsage', usage: await host.settingsContextUsage() }); break;
            case 'projectFiles': this.post({ type: 'projectFiles', files: await host.settingsProjectFiles() }); break;
            case 'pinSizes':     this.post({ type: 'pinSizes', sizes: await host.settingsPinSizes(msg.pins ?? '') }); break;
          }
        } catch (err) {
          this.log(`[settings] ${msg.type} failed: ${String(err)}`);
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
      case 'openFile': {
        // A project file opens in the editor; a folder (the rules) is shown in the explorer.
        if (!msg.path) {
          return;
        }
        const uri = vscode.Uri.file(msg.path);
        try {
          const stat = await vscode.workspace.fs.stat(uri);
          if (stat.type & vscode.FileType.Directory) {
            await vscode.commands.executeCommand('revealInExplorer', uri);
          } else {
            await vscode.window.showTextDocument(uri, { preview: false });
          }
        } catch (err) {
          this.log(`[settings] open ${msg.path} failed: ${String(err)}`);
          this.post({ type: 'error', message: String(err instanceof Error ? err.message : err) });
        }
        return;
      }
      case 'openXray':
        this.openXray();
        return;
      case 'browsePinned': {
        const picked = await vscode.window.showOpenDialog({ canSelectMany: false, canSelectFiles: true, canSelectFolders: false });
        if (picked?.[0]) {
          this.post({ type: 'pinnedPicked', path: picked[0].fsPath });
        }
        return;
      }
      case 'save': {
        if (!host?.isRunning || !msg.json) {
          // Clicking Save cannot say nothing: with no host nothing is written, and the panel kept
          // its previous status — so "Settings saved." if you had saved once before.
          this.post({ type: 'error', message: hostUnavailableMessage() });
          return;
        }
        try {
          const before = this.parseKeys(this.lastConfigJson);
          // The panel sends back the whole object it opened with: the host applies only what differs
          // from it, so a change made elsewhere since then is not reverted.
          const result = await host.configUpdate(msg.json, this.lastConfigJson);
          this.lastConfigJson = msg.json;
          // The rules field IS saved: it is some of its lines that are inert. Said here, not only
          // in /diagnostics, which nobody opens after writing a rule they believe is in place.
          this.post({ type: 'saveDone', ok: true, rulesIgnored: result?.permissionRulesIgnored ?? 0 });
          this.onSaved();

          const after = this.parseKeys(msg.json);
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
        } catch (err) {
          this.log(`[settings] save failed: ${String(err)}`);
          this.post({ type: 'error', message: hostErrorText(err) });
        }
        return;
      }
    }
  }

  private parseKeys(json: string): { provider?: string; baseUrl?: string; language?: string } | null {
    try {
      return JSON.parse(json) as { provider?: string; baseUrl?: string; language?: string };
    } catch {
      return null;
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
    };
  }

  private renderHtml(webview: vscode.Webview): string {
    const script = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'settings.js'));
    const style = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'media', 'settings.css'));
    const nonce = crypto.randomBytes(16).toString('base64');
    const l10n = JSON.stringify(SettingsPanel.strings()).replace(/</g, '\\u003c');
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
<script nonce="${nonce}">window.__l10n = ${l10n};</script>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }
}
