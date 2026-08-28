// Editor side of the reverse `debug/*` surface (roadmap §21): drives vscode.debug and the Debug
// Adapter Protocol on behalf of the host's debug_control / debug_inspect tools.
//
// The Visual Studio front-end does the same job through EnvDTE inside devenv; the §21 probe drove
// both APIs through the same seven conditions before either adapter existed, and VS Code was the
// faster of the two on the inner loop. Three facts it measured are load-bearing here and are marked
// where they apply: values are rendered by the adapter and must never be parsed, the thread id comes
// from the `stopped` event and is never assumed, and a raw stack is mostly runtime noise.
import * as vscode from 'vscode';
import {
  DebugBreakpointDto,
  DebugCaptureTestParams,
  DebugEvaluateParams,
  DebugFrameDto,
  DebugStartDto,
  DebugStepParams,
  DebugStopStateDto,
  DebugVariableDto,
} from './protocol';

/** Reverse-RPC surface for the debugger; wired only when the host is told we serve it. */
export interface DebugDelegate {
  addBreakpoint(file: string, line: number): Promise<DebugBreakpointDto | null>;
  removeBreakpoint(file: string, line: number): Promise<boolean>;
  listBreakpoints(): Promise<DebugBreakpointDto[]>;
  start(): Promise<DebugStartDto>;
  continue(): Promise<DebugStopStateDto | null>;
  step(p: DebugStepParams): Promise<DebugStopStateDto | null>;
  state(): Promise<DebugStopStateDto | null>;
  evaluate(p: DebugEvaluateParams): Promise<string | null>;
  stop(): Promise<void>;
  captureTest(p: DebugCaptureTestParams): Promise<DebugStopStateDto | null>;
}

/** How long a resume may run before we answer "no stop" rather than hang the agent's turn. */
const RESUME_TIMEOUT_MS = 120_000;
/** A launch has to build first, so it gets the same generous budget as the Visual Studio side. */
const START_TIMEOUT_MS = 300_000;
/** Caps mirroring the Core formatter's, applied here so the wire stays small too. */
const MAX_FRAMES = 24;
const MAX_LOCALS = 40;

type Transition = 'stopped' | 'ended' | 'timeout';

export class DebugBridge implements DebugDelegate, vscode.Disposable {
  private readonly disposables: vscode.Disposable[] = [];

  /** Thread reported by the last `stopped` event — never assumed to be 1 (the probe saw 0). */
  private stoppedThreadId: number | undefined;
  /** Reason/text of the last `stopped` event (§25 capture reads the exception identity here). */
  private lastStop: { reason: string; text: string | null } | undefined;
  /** Resolvers waiting for the next stop-or-end transition. */
  private waiters: Array<(t: Transition) => void> = [];

  constructor(private readonly log?: (line: string) => void) {
    // The DAP traffic is the only place `stopped` and `continued` are visible: the vscode API
    // exposes sessions, not their execution state.
    this.disposables.push(
      vscode.debug.registerDebugAdapterTrackerFactory('*', {
        createDebugAdapterTracker: () => ({
          onDidSendMessage: (m: { type?: string; event?: string; body?: { threadId?: number } }) => {
            if (m.type !== 'event') {
              return;
            }
            if (m.event === 'stopped') {
              const body = m.body as { threadId?: number; reason?: string; text?: string; description?: string };
              this.stoppedThreadId = body?.threadId ?? 0;
              // §25: an exception stop carries its identity in the event body — kept for the
              // capture, since no later request re-surfaces it.
              this.lastStop = { reason: body?.reason ?? 'stopped', text: body?.text ?? body?.description ?? null };
              this.release('stopped');
            } else if (m.event === 'continued') {
              this.stoppedThreadId = undefined;
            }
          },
        }),
      }),
    );

    this.disposables.push(
      vscode.debug.onDidTerminateDebugSession(() => {
        this.stoppedThreadId = undefined;
        this.release('ended');
      }),
    );
  }

  dispose(): void {
    this.release('ended');
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }

  // ── Breakpoints ───────────────────────────────────────────────────────────

  async addBreakpoint(file: string, line: number): Promise<DebugBreakpointDto | null> {
    const location = new vscode.Location(vscode.Uri.file(file), new vscode.Position(Math.max(0, line - 1), 0));
    const before = vscode.debug.breakpoints.length;
    vscode.debug.addBreakpoints([new vscode.SourceBreakpoint(location, true)]);

    // Report it as the debugger holds it, not as it was asked for: a breakpoint may bind to
    // another line, and one that was already there is not a failure.
    const bound = this.findBreakpoints(file, line);
    if (bound.length > 0) {
      return bound[0];
    }
    return vscode.debug.breakpoints.length > before ? { file, line, enabled: true } : null;
  }

  async removeBreakpoint(file: string, line: number): Promise<boolean> {
    const doomed = vscode.debug.breakpoints.filter((b) => this.matches(b, file, line));
    if (doomed.length === 0) {
      return false;
    }
    vscode.debug.removeBreakpoints(doomed);
    return true;
  }

  async listBreakpoints(): Promise<DebugBreakpointDto[]> {
    return vscode.debug.breakpoints.flatMap((b) => this.describe(b) ?? []);
  }

  private matches(bp: vscode.Breakpoint, file: string, line: number): boolean {
    const d = this.describe(bp);
    return d !== undefined && d.line === line && samePath(d.file, file);
  }

  private findBreakpoints(file: string, line: number): DebugBreakpointDto[] {
    return vscode.debug.breakpoints.flatMap((b) => {
      const d = this.describe(b);
      return d !== undefined && samePath(d.file, file) && Math.abs(d.line - line) <= 1 ? [d] : [];
    });
  }

  private describe(bp: vscode.Breakpoint): DebugBreakpointDto | undefined {
    if (!(bp instanceof vscode.SourceBreakpoint)) {
      return undefined; // function / data / exception breakpoints have no file:line to report
    }
    return {
      file: bp.location.uri.fsPath,
      line: bp.location.range.start.line + 1,
      enabled: bp.enabled,
    };
  }

  // ── Running ───────────────────────────────────────────────────────────────

  /**
   * Starts the workspace's first launch configuration and waits for the first stop.
   *
   * The three outcomes are kept apart all the way to the model: a workspace with no launch
   * configuration is by far the most common answer here, and calling it "the program ran without
   * stopping" would send the agent hunting a bug in code that never executed.
   */
  async start(): Promise<DebugStartDto> {
    if (vscode.debug.activeDebugSession) {
      return { state: null, failure: 'A debugging session is already running — continue or stop it first.' };
    }

    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
      return { state: null, failure: 'No folder is open, so there is nothing to launch.' };
    }

    const configured = vscode.workspace
      .getConfiguration('launch', folder.uri)
      .get<Array<{ name?: string; type?: string }>>('configurations', []);
    const configuration = configured.find((c) => typeof c.name === 'string' && c.name.length > 0);
    if (!configuration) {
      return {
        state: null,
        failure:
          'This workspace has no launch configuration (.vscode/launch.json), so VS Code has nothing to '
          + 'start. Ask the user to create one, or reason from the source instead.',
      };
    }

    const settled = this.nextTransition(START_TIMEOUT_MS);
    let started = false;
    try {
      started = await vscode.debug.startDebugging(folder, configuration.name!);
    } catch (err) {
      this.release('ended');
      return { state: null, failure: `VS Code refused to start "${configuration.name}": ${String(err)}` };
    }

    if (!started) {
      this.release('ended');
      return {
        state: null,
        failure:
          `VS Code could not start the "${configuration.name}" configuration. A debug extension for `
          + 'this language may be missing, or the configuration may be invalid.',
      };
    }

    const transition = await settled;
    if (transition === 'stopped') {
      return { state: await this.capture(), failure: null };
    }
    if (transition === 'ended') {
      return { state: null, failure: null }; // ran to completion — an ordinary answer
    }
    return {
      state: null,
      failure: `The session did not reach a stop within ${Math.round(START_TIMEOUT_MS / 60_000)} minute(s).`,
    };
  }

  async continue(): Promise<DebugStopStateDto | null> {
    return this.resume((session, threadId) => session.customRequest('continue', { threadId }));
  }

  async step(p: DebugStepParams): Promise<DebugStopStateDto | null> {
    const command = p.kind === 'into' ? 'stepIn' : p.kind === 'out' ? 'stepOut' : 'next';
    return this.resume((session, threadId) => session.customRequest(command, { threadId }));
  }

  /**
   * Issues a resume and waits for the *next* stop.
   *
   * The waiter is armed before the request goes out, and it keys on a transition rather than on
   * being stopped: the probe measured a resume answering in 0,016 s under VS Code, far faster than
   * any check of the current state could be trusted to happen after it.
   */
  private async resume(
    issue: (session: vscode.DebugSession, threadId: number) => Thenable<unknown>,
  ): Promise<DebugStopStateDto | null> {
    const session = vscode.debug.activeDebugSession;
    if (!session || this.stoppedThreadId === undefined) {
      return null;
    }

    const threadId = this.stoppedThreadId;
    const settled = this.nextTransition(RESUME_TIMEOUT_MS);
    try {
      await issue(session, threadId);
    } catch (err) {
      this.release('ended');
      this.log?.(`[debug] resume failed: ${String(err)}`);
      return null;
    }

    return (await settled) === 'stopped' ? this.capture() : null;
  }

  async stop(): Promise<void> {
    const session = vscode.debug.activeDebugSession;
    if (session) {
      await vscode.debug.stopDebugging(session);
    }
  }

  // ── §25: capture one failing test ─────────────────────────────────────────

  /**
   * Launches the repro runner under an ephemeral inline `coreclr` configuration (no launch.json),
   * arms the all-exceptions filter at the entry stop, and returns the first exception stop whose
   * stack reaches the workspace. Probed before being wired (2026-08-20): ~1.4 s launch → stop on
   * a warm host — but the very first launch of a cold C# extension may never start the adapter,
   * hence the entry timeout answers null rather than waiting on a session that will not come.
   */
  async captureTest(p: DebugCaptureTestParams): Promise<DebugStopStateDto | null> {
    if (vscode.debug.activeDebugSession) {
      return null; // never hijack a session the user (or the model) is driving
    }
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
      return null;
    }

    const entrySettled = this.nextTransition(START_TIMEOUT_MS);
    let started = false;
    try {
      started = await vscode.debug.startDebugging(folder, {
        type: 'coreclr',
        request: 'launch',
        name: 'Inferpal test capture',
        program: p.program,
        args: p.args,
        cwd: p.cwd,
        stopAtEntry: true,
        justMyCode: false,
        console: 'internalConsole',
        internalConsoleOptions: 'neverOpen',
      } as vscode.DebugConfiguration);
    } catch (err) {
      this.log?.(`[debug] captureTest launch failed: ${String(err)}`);
      this.release('ended');
      return null;
    }
    if (!started) {
      // Disarm the transition waiter: nothing will ever fire it, and left armed it consumed the
      // next real session's event 5 minutes later (pre-1.6.0 architecture review).
      this.release('ended');
      return null;
    }
    // Pin OUR session now: after a 5-minute entry timeout, activeDebugSession may be one the
    // USER started meanwhile — this.stop() would have killed theirs (pre-1.6.0 architecture review).
    const launched = vscode.debug.activeDebugSession as vscode.DebugSession | undefined;
    if ((await entrySettled) !== 'stopped') {
      if (launched) {
        await vscode.debug.stopDebugging(launched);
      }
      return null;
    }

    // Asserted on purpose: the early "already debugging" guard above narrows the property access
    // to undefined for the rest of the function, and the capture session created since would
    // otherwise type as `never`.
    const session = vscode.debug.activeDebugSession as vscode.DebugSession | undefined;
    if (!session) {
      return null;
    }
    try {
      // Armed mid-session, after the entry stop: probed, vsdbg accepts it (the runner would race
      // a filter set any later — the failing test throws within milliseconds of resuming).
      await session.customRequest('setExceptionBreakpoints', { filters: ['all'] });

      // Continue past stops whose stack never reaches the workspace (loader-time first-chance
      // noise), bounded — the same skip loop as the probe capturer.
      for (let hop = 0; hop < 20; hop++) {
        const settled = this.nextTransition(RESUME_TIMEOUT_MS);
        await session.customRequest('continue', { threadId: this.stoppedThreadId });
        if ((await settled) !== 'stopped') {
          return null; // ran to completion: nothing threw — an ordinary answer
        }
        const frames = await this.frames(session);
        const hit = frames.find((f) => f.file !== null && underRoot(p.projectRoot, f.file));
        if (hit) {
          const locals = await this.localsExpanded(session, hit.id);
          return {
            reason: 'exception',
            threadId: this.stoppedThreadId ?? 0,
            frames: frames.slice(0, MAX_FRAMES),
            locals,
            exception: this.lastStop?.text ?? null,
          };
        }
      }
      return null;
    } catch (err) {
      this.log?.(`[debug] captureTest failed: ${String(err)}`);
      return null;
    } finally {
      // The capture session never outlives the call, whatever happened above.
      try {
        await vscode.debug.stopDebugging(session);
      } catch {
        // already gone
      }
    }
  }

  /**
   * Locals of the frame plus one level of children for expandable values — vsdbg renders
   * collections through their debugger type proxies, so one level is where the payload lives
   * (`{[ui:theme, dark]}`); bounded so a huge object cannot flood the prompt.
   */
  private async localsExpanded(session: vscode.DebugSession, frameId: number): Promise<DebugVariableDto[]> {
    const out: DebugVariableDto[] = [];
    try {
      const scopes = (await session.customRequest('scopes', { frameId })) as {
        scopes?: Array<{ name: string; variablesReference: number; expensive?: boolean; presentationHint?: string }>;
      };
      const list = scopes?.scopes ?? [];
      const scope =
        list.find((s) => s.presentationHint === 'locals') ?? list.find((s) => s.expensive !== true) ?? list[0];
      if (!scope) {
        return out;
      }

      const top = (await session.customRequest('variables', { variablesReference: scope.variablesReference })) as {
        variables?: Array<{ name: string; value: string; type?: string; variablesReference?: number }>;
      };
      let childBudget = 40;
      for (const v of (top?.variables ?? []).slice(0, MAX_LOCALS)) {
        out.push({ name: v.name, type: v.type ?? '', value: v.value });
        if (!v.variablesReference || childBudget <= 0 || v.name === '$exception') {
          continue;
        }
        try {
          const kids = (await session.customRequest('variables', { variablesReference: v.variablesReference })) as {
            variables?: Array<{ name: string; value: string; type?: string }>;
          };
          for (const k of (kids?.variables ?? []).slice(0, 8)) {
            if (childBudget-- <= 0) {
              break;
            }
            out.push({ name: `${v.name}.${k.name}`, type: k.type ?? '', value: k.value });
          }
        } catch {
          // a value that refuses expansion keeps its flat rendering
        }
      }
    } catch (err) {
      this.log?.(`[debug] localsExpanded failed: ${String(err)}`);
    }
    return out;
  }

  // ── Reading ───────────────────────────────────────────────────────────────

  async state(): Promise<DebugStopStateDto | null> {
    return this.stoppedThreadId === undefined || !vscode.debug.activeDebugSession ? null : this.capture();
  }

  async evaluate(p: DebugEvaluateParams): Promise<string | null> {
    const session = vscode.debug.activeDebugSession;
    if (!session || this.stoppedThreadId === undefined) {
      return null;
    }

    const frameId = p.frameId ?? (await this.frames(session))[0]?.id;
    try {
      const answer = (await session.customRequest('evaluate', {
        expression: p.expression,
        frameId,
        context: 'watch',
      })) as { result?: string } | undefined;
      // An unevaluable expression is an ordinary answer, not an error — null, and the tool says so.
      return answer?.result ?? null;
    } catch (err) {
      this.log?.(`[debug] evaluate failed: ${String(err)}`);
      return null;
    }
  }

  /** Snapshots the paused debugger. Each section degrades on its own: locals must not cost the stack. */
  private async capture(): Promise<DebugStopStateDto | null> {
    const session = vscode.debug.activeDebugSession;
    const threadId = this.stoppedThreadId;
    if (!session || threadId === undefined) {
      return null;
    }

    const frames = await this.frames(session);
    return {
      // The stopped event's identity was frozen to 'break': after an exception stop, debug/state
      // and debug/step reported "break" and the exception text existed only in the captureTest
      // path (pre-1.6.0 architecture review). lastStop carries what the adapter actually said.
      reason: this.lastStop?.reason ?? 'break',
      threadId,
      frames: frames.slice(0, MAX_FRAMES),
      locals: frames.length > 0 ? await this.locals(session, frames[0].id) : [],
      exception: this.lastStop?.reason === 'exception' ? this.lastStop.text : null,
    };
  }

  private async frames(session: vscode.DebugSession): Promise<DebugFrameDto[]> {
    if (this.stoppedThreadId === undefined) {
      return [];
    }
    try {
      const answer = (await session.customRequest('stackTrace', {
        threadId: this.stoppedThreadId,
        levels: MAX_FRAMES,
      })) as { stackFrames?: Array<{ id: number; name: string; line?: number; source?: { path?: string } }> };

      return (answer?.stackFrames ?? []).map((f) => ({
        id: f.id,
        function: f.name,
        file: f.source?.path ?? null,
        line: f.line ?? null,
      }));
    } catch (err) {
      this.log?.(`[debug] stackTrace failed: ${String(err)}`);
      return [];
    }
  }

  private async locals(session: vscode.DebugSession, frameId: number): Promise<DebugVariableDto[]> {
    try {
      const scopes = (await session.customRequest('scopes', { frameId })) as {
        scopes?: Array<{ name: string; variablesReference: number; expensive?: boolean; presentationHint?: string }>;
      };

      // ⚠ Chosen by presentationHint, never by name. Scope names are the adapter's own text and are
      // localized in some of them — matching "Locals" is the §18 mistake (matching a tool's own
      // message) in another costume. The fallback is "the first cheap scope", not "the first named
      // like ours".
      const list = scopes?.scopes ?? [];
      const scope =
        list.find((s) => s.presentationHint === 'locals') ?? list.find((s) => s.expensive !== true) ?? list[0];
      if (!scope) {
        return [];
      }

      const answer = (await session.customRequest('variables', {
        variablesReference: scope.variablesReference,
      })) as { variables?: Array<{ name: string; value: string; type?: string }> };

      return (answer?.variables ?? []).slice(0, MAX_LOCALS).map((v) => ({
        name: v.name,
        // Rendered by the adapter, handed over untouched: the probe recorded the same list as
        // `Count = 3` under Visual Studio and `(3) [21, 42, 43]` here.
        type: v.type ?? '',
        value: v.value,
      }));
    } catch (err) {
      this.log?.(`[debug] variables failed: ${String(err)}`);
      return [];
    }
  }

  // ── Transitions ───────────────────────────────────────────────────────────

  private nextTransition(timeoutMs: number): Promise<Transition> {
    return new Promise<Transition>((resolve) => {
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w !== settle);
        resolve('timeout');
      }, timeoutMs);

      const settle = (t: Transition): void => {
        clearTimeout(timer);
        resolve(t);
      };
      this.waiters.push(settle);
    });
  }

  private release(t: Transition): void {
    const pending = this.waiters;
    this.waiters = [];
    for (const w of pending) {
      w(t);
    }
  }
}

function samePath(a: string, b: string): boolean {
  const normalize = (p: string): string => p.replace(/\\/g, '/').toLowerCase();
  return normalize(a) === normalize(b);
}

function underRoot(root: string, file: string): boolean {
  const normalize = (p: string): string => p.replace(/\\/g, '/').toLowerCase().replace(/\/+$/, '');
  return normalize(file).startsWith(normalize(root) + '/');
}
