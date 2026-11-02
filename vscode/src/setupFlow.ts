// The first-run card's steps, on the extension side: the host looks for a server and proposes models (`setup/*`, the
// Core's ServerScan and FirstRunPlanner); this class strings the steps together and hands the webview a view to draw.
// The webview decides nothing.
import type { HostClient } from './hostClient';
import { hostErrorText } from './hostStatus';
import type { ServerInstallCard, SetupInstallProgress, SetupPlace, SetupPullProgress, SetupReason } from './protocol';
import type { WvSetupAction, WvSetupView } from './webviewMessages';

export interface SetupFlowDeps {
  host(): HostClient | undefined;
  /** Draws the card (null: takes it away). */
  show(view: WvSetupView | null): void;
  log(line: string): void;
  /** The choice is saved: the chat takes the model, or the host restarts for a server of another type. */
  /** The card's choice is saved; `windowText`: the context window written, or why it stays, said in the chat. */
  applied(model: string, restartNeeded: boolean, windowText?: string | null): Promise<void>;
  openSettings(): Promise<void>;
  /** Puts the command the card shows on the clipboard. */
  copy(text: string): Promise<void>;
}

export class SetupFlow {
  private view: WvSetupView | null = null;
  /** The key typed for the server the card shows (none: the saved one, for the saved address only). */
  private key: string | undefined;
  /** The graphics memory chosen on the card for the server it shows (none: the host's own figure, or unknown). */
  private card: number | undefined;
  /**
   * ⚠ Every search and every gesture is a new generation, and a step whose generation is no longer the current one
   * draws nothing: a scan that ends after the person typed an address would draw over their choice — and propose the
   * scanned server with the key they typed for theirs.
   */
  private generation = 0;

  constructor(private readonly deps: SetupFlowDeps) {}

  /** The card as drawn now, to draw again after the webview reloads. */
  get current(): WvSetupView | null {
    return this.view;
  }

  /** Looks for a server and proposes what to use; `command`: `/setup`, asked by the person. */
  async start(reason: SetupReason | 'command'): Promise<void> {
    const host = this.deps.host();
    if (!host) return;
    // A download or an install in progress keeps its card: looking again would drop the model it is about to adopt, or
    // the Stop of the install and its ending.
    if (this.view?.pull || this.view?.installStep === 'running') {
      this.deps.show(this.view);
      return;
    }
    const g = ++this.generation;
    this.deps.log(`[setup] start (${reason})`);
    this.key = undefined;
    this.card = undefined;
    try {
      const strings = await host.setupStrings();
      const checking = await host.setupPlaces();
      if (g !== this.generation) return;
      this.draw({ phase: 'searching', strings, places: checking, others: [] });
      const scan = await host.setupScan();
      if (g !== this.generation) return;
      const answering = scan.places.filter((p) => p.outcome === 'answers' && p.provider);
      const offered = answering.find((p) => p.url === scan.offered);
      if (!offered) {
        this.draw({ phase: 'nothing', strings, places: scan.places, others: [], install: scan.install ?? null });
        return;
      }
      await this.propose(g, offered, answering.filter((p) => p !== offered), scan.places, undefined, undefined);
    } catch (err) {
      this.fail(g, err);
    }
  }

  async onAction(a: WvSetupAction): Promise<void> {
    const host = this.deps.host();
    const view = this.view;
    // Stop while the install runs (the card is busy) — without a new generation: the install's ending is still drawn.
    if (a.action === 'installStop') {
      if (view?.installStep !== 'running') return;
      try {
        await host?.setupInstallStop();
      } catch (err) {
        // The host gone, winget runs on out of reach: the install's own request ends on the same error, said on the card.
        this.deps.log(`[setup] stop: ${String(err)}`);
      }
      return;
    }
    if (a.action === 'copyCommand') {
      const command = view?.install?.command;
      if (command) await this.deps.copy(command);
      return;
    }
    if (!host || !view || view.busy) return;
    if (a.action === 'lookAgain') {
      await this.start('command');
      return;
    }
    const g = ++this.generation;
    try {
      switch (a.action) {
        case 'connect': {
          this.draw({ ...view, busy: true, error: null });
          const place = await host.setupExamine(a.url, a.key);
          if (g !== this.generation) return;
          if (place.outcome === 'answers' && place.provider) {
            await this.propose(g, place, [], view.places, a.key, undefined);
          } else {
            // The typed address joins the list with what it answered.
            const places = [...view.places.filter((p) => p.url !== place.url), place];
            this.draw({ ...view, phase: 'nothing', places, busy: false, error: `${place.url} — ${place.status}` });
          }
          return;
        }
        case 'use': {
          const place = view.others.find((p) => p.url === a.url);
          if (!place) return;
          this.draw({ ...view, busy: true, error: null });
          // The server shown so far becomes one of the others, offered back.
          const others = view.others.filter((p) => p !== place);
          const shown = view.places.find((p) => p.url === view.server?.url);
          if (shown) others.push(shown);
          await this.propose(g, place, others, view.places, undefined, undefined);
          return;
        }
        case 'card': {
          // The same server, planned again for the memory chosen: its key goes with it.
          if (!view.server) return;
          this.draw({ ...view, busy: true, error: null });
          await this.propose(g, view.server, view.others, view.places, this.key, a.gb);
          return;
        }
        case 'start':
        case 'chooseOther': {
          const plan = view.plan;
          if (!view.server || !plan?.chatModel) return;
          this.draw({ ...view, busy: true, error: null });
          const result = await host.setupApply({
            url: view.server.url, provider: view.server.provider, chatModel: plan.chatModel,
            completionModel: plan.completionModel ?? null, searchModel: plan.searchModel ?? null, apiKey: this.key,
            cardGb: this.card ?? null,
          });
          if (g !== this.generation) return;
          this.finish();
          await this.deps.applied(plan.chatModel, result.restartNeeded, result.windowText);
          if (a.action === 'chooseOther') await this.deps.openSettings();
          return;
        }
        case 'download': {
          const download = view.plan?.downloads.find((d) => d.tag === a.tag);
          if (!view.server || !download) return;
          const server = view.server;
          this.draw({ ...view, busy: true, error: null, pull: { title: download.downloading, status: '', percent: 0 } });
          const pulled = await host.setupPull(download.tag, server.url, server.provider, this.key);
          if (g !== this.generation) return;
          if (!pulled.ok) {
            this.draw({ ...(this.view ?? view), busy: false, pull: null, error: pulled.error ?? null });
            return;
          }
          const result = await host.setupApply({
            url: server.url, provider: server.provider, chatModel: download.tag, searchModel: view.plan?.searchModel ?? null,
            apiKey: this.key, cardGb: this.card ?? null,
          });
          if (g !== this.generation) return;
          this.finish();
          await this.deps.applied(download.tag, result.restartNeeded, result.windowText);
          return;
        }
        case 'install': {
          const card = view.install;
          if (!card?.button) return;
          // An install asks first, on the card that names the command; starting an installed Ollama is the click itself.
          if (card.kind === 'install') {
            this.draw({ ...view, installStep: 'consent', installReport: null, error: null });
            return;
          }
          await this.runInstall(g, 'start');
          return;
        }
        case 'installConfirm':
          if (view.installStep !== 'consent') return;
          await this.runInstall(g, 'install');
          return;
        case 'installCancel':
          this.draw({ ...view, installStep: null });
          return;
        case 'installRetry': {
          const report = view.installReport;
          if (!report?.retryKind) return;
          // An install again shows its command again before it runs; a start is the click itself.
          if (report.retryKind === 'install') {
            if (view.install?.consent) this.draw({ ...view, installStep: 'consent', installReport: null });
            return;
          }
          await this.runInstall(g, 'start', report.retryRunning ?? undefined);
          return;
        }
      }
    } catch (err) {
      this.fail(g, err);
    }
  }

  /** `setup/installProgress`: winget's last line, under the steps. */
  onInstallProgress(p: SetupInstallProgress): void {
    const view = this.view;
    if (view?.installStep !== 'running') return;
    this.draw({ ...view, installLine: p.line });
  }

  /**
   * Installs or starts Ollama, then looks again: the scan finds the server and the card goes on to the models as for any
   * server found. An ending without a server stays on the card, with its cause and what may work.
   */
  private async runInstall(g: number, kind: 'install' | 'start', running?: ServerInstallCard['running']): Promise<void> {
    const host = this.deps.host();
    const view = this.view;
    if (!host || !view) return;
    // A retry that starts what the scan offered to install says what a start says.
    const install = running && view.install ? { ...view.install, running } : view.install;
    this.draw({ ...view, install, busy: true, error: null, installStep: 'running', installLine: '', installReport: null });
    const report = await host.setupInstall(kind);
    if (g !== this.generation) return;
    if (report.answers || report.outcome === 'changed') {
      this.draw({ ...(this.view ?? view), busy: false, installStep: null });
      await this.start('command');
      return;
    }
    this.draw({ ...(this.view ?? view), busy: false, installStep: null, installReport: report });
  }

  /** `setup/pullProgress`: the download's line and bar. */
  onPullProgress(p: SetupPullProgress): void {
    const view = this.view;
    if (!view?.pull) return;
    this.draw({ ...view, pull: { ...view.pull, status: p.status, percent: p.percent ?? view.pull.percent ?? null } });
  }

  /** Proposes the server at `place` (its address and type are all the plan needs), with the key typed for it (none: the
   *  saved key, which the host sends to the saved address only) and the graphics memory chosen for it (none: the
   *  host's own figure). */
  private async propose(g: number, place: { url: string; provider?: string | null }, others: SetupPlace[],
                        places: SetupPlace[], key: string | undefined, card: number | undefined): Promise<void> {
    const host = this.deps.host();
    if (!host || !this.view || !place.provider) return;
    const plan = await host.setupPlan(place.url, place.provider, key, card);
    if (g !== this.generation) return;
    this.key = key;
    this.card = card;
    this.draw({
      phase: plan.chatModel ? 'found' : 'noModel', strings: this.view.strings, places,
      server: { url: place.url, provider: place.provider }, plan, others, busy: false, error: null,
    });
  }

  private draw(view: WvSetupView): void {
    this.view = view;
    this.deps.show(view);
  }

  private finish(): void {
    this.view = null;
    this.deps.show(null);
  }

  private fail(g: number, err: unknown): void {
    this.deps.log(`[setup] ${String(err)}`);
    if (g === this.generation && this.view) {
      // An install that ended on an error is no longer running: its card would otherwise stay on "running", and every
      // later search would keep it.
      this.draw({ ...this.view, busy: false, pull: null, installStep: null, error: hostErrorText(err) });
    }
  }
}
