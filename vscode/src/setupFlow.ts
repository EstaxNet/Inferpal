// The first-run card's steps, on the extension side: the host looks for a server and proposes models (`setup/*`, the
// Core's ServerScan and FirstRunPlanner); this class strings the steps together and hands the webview a view to draw.
// The webview decides nothing.
import type { HostClient } from './hostClient';
import { hostErrorText } from './hostStatus';
import type { SetupPlace, SetupPullProgress, SetupReason } from './protocol';
import type { WvSetupAction, WvSetupView } from './webviewMessages';

export interface SetupFlowDeps {
  host(): HostClient | undefined;
  /** Draws the card (null: takes it away). */
  show(view: WvSetupView | null): void;
  log(line: string): void;
  /** The choice is saved: the chat takes the model, or the host restarts for a server of another type. */
  applied(model: string, restartNeeded: boolean): Promise<void>;
  openSettings(): Promise<void>;
}

export class SetupFlow {
  private view: WvSetupView | null = null;
  /** The key typed for the server the card shows (none: the saved one, for the saved address only). */
  private key: string | undefined;
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
    // A download in progress keeps its card: looking again would drop the model it is about to adopt.
    if (this.view?.pull) {
      this.deps.show(this.view);
      return;
    }
    const g = ++this.generation;
    this.deps.log(`[setup] start (${reason})`);
    this.key = undefined;
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
        this.draw({ phase: 'nothing', strings, places: scan.places, others: [] });
        return;
      }
      await this.propose(g, offered, answering.filter((p) => p !== offered), scan.places, undefined);
    } catch (err) {
      this.fail(g, err);
    }
  }

  async onAction(a: WvSetupAction): Promise<void> {
    const host = this.deps.host();
    const view = this.view;
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
            await this.propose(g, place, [], view.places, a.key);
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
          await this.propose(g, place, others, view.places, undefined);
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
          });
          if (g !== this.generation) return;
          this.finish();
          await this.deps.applied(plan.chatModel, result.restartNeeded);
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
            apiKey: this.key,
          });
          if (g !== this.generation) return;
          this.finish();
          await this.deps.applied(download.tag, result.restartNeeded);
          return;
        }
      }
    } catch (err) {
      this.fail(g, err);
    }
  }

  /** `setup/pullProgress`: the download's line and bar. */
  onPullProgress(p: SetupPullProgress): void {
    const view = this.view;
    if (!view?.pull) return;
    this.draw({ ...view, pull: { ...view.pull, status: p.status, percent: p.percent ?? view.pull.percent ?? null } });
  }

  /** Proposes `place`, with the key typed for it (none: the saved key, which the host sends to the saved address only). */
  private async propose(g: number, place: SetupPlace, others: SetupPlace[], places: SetupPlace[],
                        key: string | undefined): Promise<void> {
    const host = this.deps.host();
    if (!host || !this.view || !place.provider) return;
    const plan = await host.setupPlan(place.url, place.provider, key);
    if (g !== this.generation) return;
    this.key = key;
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
      this.draw({ ...this.view, busy: false, pull: null, error: hostErrorText(err) });
    }
  }
}
