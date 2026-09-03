// "Inferpal Settings" panel: a singleton WebviewPanel mirroring the VS settings window
// (4 tabs — Connection / Behavior / Context / Tools). The form is rendered by
// src/webview/settings.ts from the host's config JSON (config/get); Save round-trips the
// FULL JSON through config/update (absent fields would reset — the webview mutates the
// parsed original object, never rebuilds it).
import * as vscode from 'vscode';
import * as crypto from 'crypto';
import { HostClient } from './hostClient';
import { hostUnavailableMessage, promptOpenFolder } from './hostStatus';
import { SettingsSchema } from './protocol';

interface SettingsInbound {
  type: 'ready' | 'save' | 'testConnection' | 'refreshModels';
  json?: string;
  baseUrl?: string;
}

export class SettingsPanel {
  private static current: SettingsPanel | undefined;

  private lastConfigJson = '';

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly extensionUri: vscode.Uri,
    private readonly getHost: () => HostClient | undefined,
    private readonly hasConversation: () => boolean,
    private readonly onSaved: () => void,
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
    hasConversation: () => boolean,
    onSaved: () => void,
    log: (line: string) => void,
  ): void {
    if (SettingsPanel.current) {
      SettingsPanel.current.panel.reveal();
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      'inferpal.settings',
      vscode.l10n.t('Inferpal Settings'),
      vscode.ViewColumn.Active,
      { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')] },
    );
    SettingsPanel.current = new SettingsPanel(panel, extensionUri, getHost, hasConversation, onSaved, log);
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
            // returns an empty list through the success path (measured 2026-09-03). Both end up
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
          this.post({ type: 'error', message: String(err) });
        }
        return;
      }
      case 'testConnection': {
        if (!host?.isRunning) {
          this.post({ type: 'testResult', ok: false });
          return;
        }
        try {
          // Checks the host's configured URL (like the VS Test button, which probes after save).
          const ok = await host.connectionCheck();
          this.post({ type: 'testResult', ok });
        } catch {
          this.post({ type: 'testResult', ok: false });
        }
        return;
      }
      case 'refreshModels': {
        if (!host?.isRunning) {
          return;
        }
        try {
          this.post({ type: 'models', models: await host.modelsList() });
        } catch (err) {
          this.log(`[settings] models/list failed: ${String(err)}`);
        }
        return;
      }
      case 'save': {
        if (!host?.isRunning || !msg.json) {
          return;
        }
        // config/update resets the host conversation context — confirm when one is running.
        if (this.hasConversation()) {
          const proceed = await vscode.window.showWarningMessage(
            vscode.l10n.t('Saving the settings resets the conversation context. Continue?'),
            { modal: true },
            vscode.l10n.t('Save'),
          );
          if (!proceed) {
            this.post({ type: 'saveDone', ok: false });
            return;
          }
        }
        try {
          const before = this.parseKeys(this.lastConfigJson);
          await host.configUpdate(msg.json);
          this.lastConfigJson = msg.json;
          this.post({ type: 'saveDone', ok: true });
          this.onSaved();

          // Provider/BaseUrl changes only take effect after a new `initialize`.
          const after = this.parseKeys(msg.json);
          if (before && after && (before.provider !== after.provider || before.baseUrl !== after.baseUrl)) {
            const restart = await vscode.window.showInformationMessage(
              vscode.l10n.t('Provider or server URL changed — restart the Inferpal host to apply it.'),
              vscode.l10n.t('Restart'),
            );
            if (restart) {
              void vscode.commands.executeCommand('inferpal.restartHost');
            }
          }
        } catch (err) {
          this.log(`[settings] save failed: ${String(err)}`);
          this.post({ type: 'error', message: String(err) });
        }
        return;
      }
    }
  }

  private parseKeys(json: string): { provider?: string; baseUrl?: string } | null {
    try {
      return JSON.parse(json) as { provider?: string; baseUrl?: string };
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
    const t = vscode.l10n.t;
    return {
      'Tools': t('Tools'),
      'Save': t('Save'),
      'Settings saved.': t('Settings saved.'),
      'Loading settings…': t('Loading settings…'),
      'Connected': t('Connected'),
      'Backend unreachable': t('Backend unreachable'),
      'Refresh models': t('Refresh models'),
      'Show all models': t('Show all models'),
      'No model listed — is the backend reachable?': t('No model listed — is the backend reachable?'),
      'No match': t('No match'),
      'Inline diff preview for code actions': t('Inline diff preview for code actions'),
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
