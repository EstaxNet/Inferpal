// The settings form's other half: what the webview (src/webview/settings.ts) asks, answered from the host. Shared by
// the VS Code panel (settingsPanel.ts) and the settings window an Agent Client Protocol client opens in a browser
// (`Inferpal.Host --settings`, src/webview/settingsStandalone.ts): one form, one bridge, two shells around it.
// ⚠ No `vscode` import here: this file is bundled into a browser page too. What only the surrounding app can do —
// open a file, pick one, name an absent host in its own words — is the shell's.
import type {
  ApprovalRuleTable, ConfigUpdateResult, ConnectionCheckResult, ContextUsage, IndexCard, LoadedModels, McpCardsResult,
  ModelsByKind, ModelSuggestion, PinnedFileSize, ProjectFileRow, RepoInstructionRow, SettingsDocs, SettingsExclusions, SettingsSchema,
} from './protocol';

/** The host calls the settings form makes (HostClient has them all, with the same names). */
export interface SettingsHost {
  readonly isRunning: boolean;
  configGet(): Promise<string>;
  modelsListByKind(overrides?: { baseUrl?: string; provider?: string; apiKey?: string }): Promise<ModelsByKind>;
  settingsStrings(): Promise<Record<string, string>>;
  settingsSchema(): Promise<SettingsSchema>;
  connectionCheck(baseUrl?: string, apiKey?: string): Promise<ConnectionCheckResult>;
  mcpCards(): Promise<McpCardsResult>;
  mcpRetry(): Promise<McpCardsResult>;
  mcpAuthorize(name: string): Promise<McpCardsResult>;
  permissionsTable(rules: string): Promise<ApprovalRuleTable>;
  permissionsNewRule(allow: boolean, tool: string, pattern: string): Promise<string | null>;
  settingsIndexCard(): Promise<IndexCard>;
  settingsIndexRebuild(): Promise<IndexCard>;
  settingsExclusions(): Promise<SettingsExclusions>;
  settingsDocsSites(): Promise<SettingsDocs>;
  settingsDocsAction(verb: 'add' | 'reindex' | 'remove', arg: string): Promise<SettingsDocs>;
  settingsContextUsage(): Promise<ContextUsage>;
  settingsProjectFiles(): Promise<ProjectFileRow[]>;
  settingsRepoInstructions(): Promise<RepoInstructionRow[]>;
  settingsPinSizes(pins: string): Promise<PinnedFileSize[]>;
  settingsLoadedModels(): Promise<LoadedModels>;
  settingsUnloadModels(names?: string[]): Promise<LoadedModels>;
  settingsSuggestModels(form: {
    baseUrl?: string; provider?: string; apiKey?: string; vramBudgetGb?: string; current: Record<string, string>;
  }): Promise<ModelSuggestion>;
  configUpdate(json: string, base?: string): Promise<ConfigUpdateResult>;
}

/** What only the app around the form can do, in its own words. */
export interface SettingsShell {
  host(): SettingsHost | undefined;
  post(message: unknown): void;
  log(line: string): void;
  /** The host is being started: its form comes on its own (never "restart" — that would kill the start). */
  hostStarting(): boolean;
  startingMessage(): string;
  /** Why there is no host here, and the way out. */
  unavailableMessage(): string;
  /** Called with {@link unavailableMessage} when the form cannot load at all: the shell may offer its way out. */
  onHostMissing(): void;
  /** Text of a failure: the error's message — for a JSON-RPC error, the host's translated text. */
  errorText(err: unknown): string;
  openFile(path: string): Promise<void>;
  /** A file picked by the user, or undefined. */
  browseFile(): Promise<string | undefined>;
  openXray(): void;
  /** After a save the host accepted: what changed may need the app (a new language, a new server). */
  saved(before: SavedKeys | null, after: SavedKeys | null): Promise<void>;
}

/** The settings a save compares before and after. */
export interface SavedKeys { provider?: string; baseUrl?: string; language?: string }

export interface SettingsInbound {
  type: 'ready' | 'save' | 'testConnection' | 'refreshModels'
    | 'mcpCards' | 'mcpRetry' | 'mcpAuthorize' | 'rulesTable' | 'newRule' | 'browsePinned'
    | 'indexCard' | 'indexRebuild' | 'exclusions' | 'docsSites' | 'docsAction' | 'contextUsage' | 'projectFiles'
    | 'repoInstructions'
    | 'pinSizes' | 'openFile' | 'openXray' | 'loadedModels' | 'unloadModels' | 'suggestModels';
  /** unloadModels: the models to unload; absent = every loaded one. */
  names?: string[];
  /** suggestModels: the card's memory as typed, and the form's model fields by configuration key. */
  vramBudgetGb?: string;
  current?: Record<string, string>;
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

export class SettingsBridge {
  private lastConfigJson = '';
  /** Whether the form was sent. Until then the host coming back loads it; a form already shown is never reloaded — it
   *  may hold unsaved edits. */
  private loaded = false;
  private loading: Promise<void> | undefined;

  constructor(private readonly shell: SettingsShell) {}

  /** The host is up (or its start failed): a form not loaded yet loads now, one already shown is left alone. */
  loadIfNotLoaded(): void {
    if (!this.loaded) {
      void this.load();
    }
  }

  private load(): Promise<void> {
    return (this.loading ??= this.loadCore().finally(() => {
      this.loading = undefined;
    }));
  }

  private async loadCore(): Promise<void> {
    const host = this.shell.host();
    if (!host?.isRunning) {
      if (this.shell.hostStarting()) {
        // Starting is not stopped: "Restart Host" would kill the start in progress, and the form comes on its own.
        this.shell.post({ type: 'error', message: this.shell.startingMessage() });
        return;
      }
      // Same two states as the chat bubble — an empty form telling the user to restart a
      // host that no folder allowed to start is the worst of the two wordings.
      this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
      this.shell.onHostMissing();
      return;
    }
    try {
      this.lastConfigJson = await host.configGet();
      let models: ModelsByKind = { chat: [], embedding: [] };
      try {
        models = await host.modelsListByKind();
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
        this.shell.log(`[settings] settings/strings failed: ${String(err)}`);
      }
      // The form itself is declared in the Core and served over RPC: adding a setting no
      // longer means editing a TypeScript table too.
      let schema: SettingsSchema | null = null;
      try {
        schema = await host.settingsSchema();
      } catch (err) {
        this.shell.log(`[settings] settings/schema failed: ${String(err)}`);
      }
      this.shell.post({ type: 'init', configJson: this.lastConfigJson, models: models.chat,
                        embeddingModels: models.embedding, strings, schema });
      // A form the webview could not render (no schema) is not loaded: the host's next start tries again.
      this.loaded = (schema?.tabs.length ?? 0) > 0;
    } catch (err) {
      this.shell.post({ type: 'error', message: this.shell.errorText(err) });
    }
  }

  async onMessage(msg: SettingsInbound): Promise<void> {
    const host = this.shell.host();
    switch (msg.type) {
      case 'ready':
        await this.load();
        return;
      case 'testConnection': {
        if (!host?.isRunning) {
          // ⚠ On its own, `ok: false` reads as "Backend unreachable" — the wrong cause when it is
          // the HOST that is gone, and it sends the user to check a server that is answering. The
          // result clears the probe, the message names what actually failed.
          this.shell.post({ type: 'testResult', ok: false, provider: null });
          this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          // Probe the URL the user is LOOKING AT - the one in the form - and name the backend that
          // answered, exactly like the Visual Studio Test button (which reads BaseUrl from its own
          // form, auto-selects the detected provider, then refreshes models from that URL).
          const result = await host.connectionCheck(msg.baseUrl, msg.apiKey);
          this.shell.post({ type: 'testResult', ok: result.ok, provider: result.provider, refused: result.refused ?? null });
        } catch (err) {
          // Same reading, one level down: an unreachable backend does not throw — it answers
          // `ok: false` through the success path. A throw here is the host, and saying "Backend
          // unreachable" about it points at the one thing that is not broken.
          this.shell.post({ type: 'testResult', ok: false, provider: null });
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
        }
        return;
      }
      case 'refreshModels': {
        if (!host?.isRunning) {
          // ⚠ A bare return here is a ↻ button that does NOTHING: indistinguishable from a backend
          // serving no model. The 'ready' and 'testConnection' cases already name that state;
          // this one and 'save' did not.
          this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          const listed = await host.modelsListByKind({
            baseUrl: msg.baseUrl, provider: msg.provider, apiKey: msg.apiKey,
          });
          this.shell.post({ type: 'models', models: listed.chat, embeddingModels: listed.embedding });
        } catch (err) {
          // ⚠ The other branch of the same ↻ button: a refresh that fails leaves the list exactly
          // as it was, which reads as "the backend serves these and no more". The log keeps the
          // cause for whoever opens the channel; the message is what the clicker gets.
          this.shell.log(`[settings] models/list failed: ${String(err)}`);
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
        }
        return;
      }
      case 'mcpCards':
      case 'mcpRetry':
      case 'mcpAuthorize': {
        if (!host?.isRunning) {
          this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          const result = msg.type === 'mcpRetry' ? await host.mcpRetry()
            : msg.type === 'mcpAuthorize' ? await host.mcpAuthorize(msg.name ?? '')
            : await host.mcpCards();
          this.shell.post({ type: 'mcpCards', result });
        } catch (err) {
          // The cards would otherwise keep their last state, which reads as "nothing changed".
          this.shell.log(`[settings] ${msg.type} failed: ${String(err)}`);
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
        }
        return;
      }
      case 'rulesTable': {
        if (!host?.isRunning) {
          return;   // the text editor stays usable; 'ready' already said the host is gone
        }
        try {
          this.shell.post({ type: 'rulesTable', table: await host.permissionsTable(msg.rules ?? ''), requestId: msg.requestId });
        } catch (err) {
          this.shell.log(`[settings] permissions/table failed: ${String(err)}`);
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
        }
        return;
      }
      case 'newRule': {
        if (!host?.isRunning) {
          this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          const line = await host.permissionsNewRule(msg.allow === true, msg.tool ?? '', msg.pattern ?? '');
          this.shell.post({ type: 'newRule', line, requestId: msg.requestId });
        } catch (err) {
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
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
      case 'repoInstructions':
      case 'pinSizes':
      case 'loadedModels':
      case 'unloadModels':
      case 'suggestModels': {
        // The live blocks of the pages: each asks the host for what it shows, through the Core presenters.
        if (!host?.isRunning) {
          this.shell.post({ type: 'error', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          switch (msg.type) {
            case 'indexCard':    this.shell.post({ type: 'indexCard', card: await host.settingsIndexCard() }); break;
            case 'indexRebuild': this.shell.post({ type: 'indexCard', card: await host.settingsIndexRebuild() }); break;
            case 'exclusions':   this.shell.post({ type: 'exclusions', exclusions: await host.settingsExclusions() }); break;
            case 'docsSites':    this.shell.post({ type: 'docsSites', docs: await host.settingsDocsSites() }); break;
            case 'docsAction':
              this.shell.post({ type: 'docsSites', docs: await host.settingsDocsAction(msg.verb ?? 'reindex', msg.arg ?? '') });
              break;
            case 'contextUsage': this.shell.post({ type: 'contextUsage', usage: await host.settingsContextUsage() }); break;
            case 'projectFiles': this.shell.post({ type: 'projectFiles', files: await host.settingsProjectFiles() }); break;
            case 'repoInstructions':
              this.shell.post({ type: 'repoInstructions', rows: await host.settingsRepoInstructions() });
              break;
            case 'pinSizes':     this.shell.post({ type: 'pinSizes', sizes: await host.settingsPinSizes(msg.pins ?? '') }); break;
            case 'loadedModels': this.shell.post({ type: 'loadedModels', models: await host.settingsLoadedModels() }); break;
            case 'unloadModels': this.shell.post({ type: 'loadedModels', models: await host.settingsUnloadModels(msg.names) }); break;
            case 'suggestModels':
              // The FORM's server and values, as the refresh button sends them: a suggestion is about what is on screen.
              this.shell.post({
                type: 'suggestModels',
                suggestion: await host.settingsSuggestModels({
                  baseUrl: msg.baseUrl, provider: msg.provider, apiKey: msg.apiKey,
                  vramBudgetGb: msg.vramBudgetGb, current: msg.current ?? {},
                }),
              });
              break;
          }
        } catch (err) {
          this.shell.log(`[settings] ${msg.type} failed: ${String(err)}`);
          this.shell.post({ type: 'error', message: this.shell.errorText(err) });
        }
        return;
      }
      case 'openFile': {
        // A project file opens in the editor; a folder (the rules) is shown in the explorer.
        if (!msg.path) {
          return;
        }
        try {
          await this.shell.openFile(msg.path);
        } catch (err) {
          this.shell.log(`[settings] open ${msg.path} failed: ${String(err)}`);
          this.shell.post({ type: 'error', message: String(err instanceof Error ? err.message : err) });
        }
        return;
      }
      case 'openXray':
        this.shell.openXray();
        return;
      case 'browsePinned': {
        const picked = await this.shell.browseFile();
        if (picked) {
          this.shell.post({ type: 'pinnedPicked', path: picked });
        }
        return;
      }
      case 'save': {
        if (!host?.isRunning || !msg.json) {
          // Clicking Save cannot say nothing: with no host nothing is written, and the panel kept
          // its previous status — so "Settings saved." if you had saved once before.
          this.shell.post({ type: 'error', op: 'save', message: this.shell.unavailableMessage() });
          return;
        }
        try {
          const before = SettingsBridge.parseKeys(this.lastConfigJson);
          // The panel sends back the whole object it opened with: the host applies only what differs
          // from it, so a change made elsewhere since then is not reverted.
          const result = await host.configUpdate(msg.json, this.lastConfigJson);
          this.lastConfigJson = msg.json;
          // The rules field IS saved: it is some of its lines that are inert. Said here, not only
          // in /diagnostics, which nobody opens after writing a rule they believe is in place.
          this.shell.post({ type: 'saveDone', ok: true, rulesIgnored: result?.permissionRulesIgnored ?? 0 });
          await this.shell.saved(before, SettingsBridge.parseKeys(msg.json));
        } catch (err) {
          this.shell.log(`[settings] save failed: ${String(err)}`);
          this.shell.post({ type: 'error', op: 'save', message: this.shell.errorText(err) });
        }
        return;
      }
    }
  }

  private static parseKeys(json: string): SavedKeys | null {
    try {
      return JSON.parse(json) as SavedKeys;
    } catch {
      return null;
    }
  }
}
