// The settings window an Agent Client Protocol client opens (`Inferpal.Host --settings`): the VS Code settings form, in a
// browser page the host serves. This module plays the extension's part, and is evaluated BEFORE the form
// (settingsStandalone.ts imports it first): the form calls acquireVsCodeApi() as it loads.
//   form → acquireVsCodeApi().postMessage → the shared bridge (settingsBridge.ts) → POST <page>/rpc → the host
//   host → bridge → window.postMessage → form
import { SettingsBridge, SettingsHost, SettingsInbound } from '../settingsBridge';

/** What the page's server writes into it, in the interface's language. */
interface StandalonePage {
  /** The server that serves this page has stopped. */
  gone: string;
  /** A browser cannot hand a file's path to Inferpal. */
  noPicker: string;
  /** The X-Ray lives in an editor's chat panel. */
  noXray: string;
}

declare global {
  interface Window {
    __inferpal?: StandalonePage;
    acquireVsCodeApi?: () => { postMessage(message: unknown): void; getState(): unknown; setState(state: unknown): void };
  }
}

const page: StandalonePage = window.__inferpal ?? { gone: 'The settings server has stopped.', noPicker: '', noXray: '' };
// Every request goes under the page's own address, which carries its access token.
const base = location.pathname.endsWith('/') ? location.pathname : location.pathname + '/';

/** One JSON-RPC call to the host, through the page's server. A network failure is the server gone. */
async function call<T>(method: string, params?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await fetch(base + 'rpc', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ method, params: params ?? null }),
    });
  } catch {
    throw new Error(page.gone);
  }
  const body = (await response.json().catch(() => ({}))) as { result?: T; error?: { message?: string } };
  if (!response.ok || body.error) {
    throw new Error(body.error?.message || `HTTP ${response.status}`);
  }
  return body.result as T;
}

/** The host calls, by the names and parameters hostClient.ts sends them. */
const host: SettingsHost = {
  isRunning: true,
  configGet: () => call('config/get'),
  modelsListByKind: (overrides) => call('models/listByKind', overrides ?? {}),
  settingsStrings: () => call('settings/strings'),
  settingsSchema: () => call('settings/schema'),
  connectionCheck: (baseUrl, apiKey) => call('connection/check', { baseUrl, apiKey }),
  mcpCards: () => call('mcp/cards'),
  mcpRetry: () => call('mcp/retry'),
  mcpAuthorize: (name) => call('mcp/authorize', { name }),
  permissionsTable: (rules) => call('permissions/table', { rules }),
  permissionsNewRule: (allow, tool, pattern) => call('permissions/newRule', { allow, tool, pattern }),
  settingsIndexCard: () => call('settings/indexCard'),
  settingsIndexRebuild: () => call('settings/indexRebuild'),
  settingsExclusions: () => call('settings/exclusions'),
  settingsDocsSites: () => call('settings/docsSites'),
  settingsDocsAction: (verb, arg) => call('settings/docsAction', { verb, arg }),
  settingsContextUsage: () => call('settings/contextUsage'),
  settingsProjectFiles: () => call('settings/projectFiles'),
  settingsRepoInstructions: () => call('settings/repoInstructions'),
  settingsPinSizes: (pins) => call('settings/pinSizes', { pins }),
  settingsLoadedModels: () => call('settings/loadedModels'),
  settingsUnloadModels: (names) => call('settings/unloadModels', { names: names ?? null }),
  settingsSuggestModels: (form) => call('settings/suggestModels', form),
  configUpdate: (json, baseJson) => call('config/update', { json, base: baseJson }),
};

const post = (message: unknown): void => window.postMessage(message, '*');

const bridge = new SettingsBridge({
  host: () => host,
  post,
  log: (line) => console.warn(line),
  hostStarting: () => false,
  startingMessage: () => '',
  unavailableMessage: () => page.gone,
  onHostMissing: () => undefined,
  errorText: (err) => (err instanceof Error ? err.message : String(err)).trim() || page.gone,
  // The file opens in the application the system gives it — the page's server asks, the browser cannot.
  openFile: (path) => call('gui/open', { path }),
  browseFile: async () => {
    post({ type: 'error', message: page.noPicker });
    return undefined;
  },
  openXray: () => post({ type: 'error', message: page.noXray }),
  // A new language: the page is drawn again by the server, in it (the host switched with the save).
  saved: async (before, after) => {
    if ((before?.language ?? '') !== (after?.language ?? '')) {
      location.reload();
    }
  },
});

window.acquireVsCodeApi = () => ({
  postMessage: (message: unknown) => void bridge.onMessage(message as SettingsInbound),
  getState: () => undefined,
  setState: () => undefined,
});

// The theme the system uses, as the classes VS Code sets on a webview: the form's theme cards mark the one in use.
const dark = window.matchMedia('(prefers-color-scheme: dark)');
const forced = window.matchMedia('(forced-colors: active)');
const applyTheme = (): void => {
  document.body.classList.remove('vscode-light', 'vscode-dark', 'vscode-high-contrast');
  document.body.classList.add(forced.matches ? 'vscode-high-contrast' : dark.matches ? 'vscode-dark' : 'vscode-light');
};
applyTheme();
dark.addEventListener('change', applyTheme);
forced.addEventListener('change', applyTheme);

// The server lives while a page does: each tab tells it, under its own id, that it is still open, and when it closes —
// the close of one tab never ends the server under another.
const pageId = Math.random().toString(36).slice(2) + Date.now().toString(36);
const alive = (): void => {
  void fetch(base + 'alive?page=' + pageId, { method: 'POST' }).catch(() => undefined);
};
alive();
setInterval(alive, 5000);
window.addEventListener('pagehide', () => navigator.sendBeacon(base + 'close?page=' + pageId));
