import * as vscode from 'vscode';
import { t } from './i18n';
import { readableOutput, TailBuffer, terminalAttachment } from './terminalText';

/** How much of a command's output is kept: its end, where the verdict is — within one chip's size. */
const KEPT = 40_000;

/** How long the end of a command may lag behind the end of its output before its exit code is taken as unknown. */
const END_WAIT_MS = 2_000;

interface FinishedCommand {
  command: string;
  cwd: string | undefined;
  output: string;
  cut: boolean;
  exitCode: number | undefined;
  /** When it finished: of the other terminals, the latest is named. */
  at: number;
}

/**
 * The last command of each terminal `@terminal` was asked in, as it attaches it.
 *
 * ⚠ A command's output can be read only from its START: `read()` gives what is written after the call. So a terminal's
 * commands are followed as they start — and only once `@terminal` has been asked in that terminal: from its first
 * `read()`, VS Code keeps every chunk of the command's output for as long as the command runs (a dev server left running
 * for hours), whatever this class keeps of it. Followed everywhere, every user would pay that for a mention they may
 * never type; asked first, the person runs the command again — said by the notice, as for a command older than
 * Inferpal.
 */
export class TerminalHistory implements vscode.Disposable {
  private readonly followed = new Set<vscode.Terminal>();
  private readonly last = new Map<vscode.Terminal, FinishedCommand>();
  private readonly running = new Map<vscode.Terminal, number>();
  /** Resolved when the terminal closes: a command it was running then never ends, and its reading must stop. */
  private readonly closing = new Map<vscode.Terminal, { closed: Promise<void>; close: () => void }>();
  private readonly subscriptions: vscode.Disposable[];

  constructor(private readonly log: (line: string) => void) {
    this.subscriptions = [
      vscode.window.onDidStartTerminalShellExecution((e) => {
        if (this.followed.has(e.terminal)) {
          void this.follow(e);
        }
      }),
      vscode.window.onDidCloseTerminal((terminal) => {
        this.followed.delete(terminal);
        this.last.delete(terminal);
        this.running.delete(terminal);
        this.closing.get(terminal)?.close();
        this.closing.delete(terminal);
      }),
    ];
  }

  dispose(): void {
    for (const s of this.subscriptions) {
      s.dispose();
    }
    for (const c of this.closing.values()) {
      c.close();
    }
  }

  private closedSignal(terminal: vscode.Terminal): Promise<void> {
    let entry = this.closing.get(terminal);
    if (!entry) {
      let close: () => void = () => undefined;
      const closed = new Promise<void>((resolve) => { close = resolve; });
      entry = { closed, close };
      this.closing.set(terminal, entry);
    }
    return entry.closed;
  }

  /** Reads one command from its start to its end, then keeps it as its terminal's last. */
  private async follow(e: vscode.TerminalShellExecutionStartEvent): Promise<void> {
    const buffer = new TailBuffer(KEPT);
    // ⚠ Called before the first await: the output written after this call is all that can be read.
    const stream = e.execution.read()[Symbol.asyncIterator]();
    const closed = this.closedSignal(e.terminal).then(() => 'closed' as const);
    this.running.set(e.terminal, (this.running.get(e.terminal) ?? 0) + 1);
    let resolveEnd: (code: number | undefined) => void = () => undefined;
    const ended = new Promise<number | undefined>((resolve) => { resolveEnd = resolve; });
    const subscription = vscode.window.onDidEndTerminalShellExecution((end) => {
      if (end.execution === e.execution) {
        resolveEnd(end.exitCode);
      }
    });
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      // A terminal closed mid-command ends nothing: its stream would wait forever, holding this reading and its output.
      for (;;) {
        const next = await Promise.race([stream.next(), closed]);
        if (next === 'closed' || next.done) {
          break;
        }
        buffer.append(next.value);
      }
      const exitCode = await Promise.race([
        ended, closed.then(() => undefined),
        new Promise<undefined>((r) => { timer = setTimeout(() => r(undefined), END_WAIT_MS); }),
      ]);
      // Closed meanwhile: its command is no one's to attach.
      if (!vscode.window.terminals.includes(e.terminal)) {
        return;
      }
      const command = e.execution.commandLine.value.trim();
      const output = readableOutput(buffer.kept());
      // An Enter on an empty prompt is reported as a command (PowerShell): it is not the one the person ran.
      if (command.length === 0 && output.trim().length === 0) {
        return;
      }
      this.last.set(e.terminal, {
        // Read at the end: the command line can be more exact once it has run.
        command,
        cwd: e.execution.cwd?.fsPath,
        output,
        cut: buffer.dropped() > 0,
        exitCode,
        at: Date.now(),
      });
    } catch (err) {
      this.log(`[terminal] a command's output was not read: ${String(err)}`);
    } finally {
      clearTimeout(timer);
      subscription.dispose();
      const left = (this.running.get(e.terminal) ?? 1) - 1;
      if (left > 0) {
        this.running.set(e.terminal, left);
      } else {
        this.running.delete(e.terminal);
      }
    }
  }

  /** What `@terminal` attaches — the ACTIVE terminal's last finished command — or why nothing; asked in a terminal not
   *  followed yet, it starts following it.
   *  ⚠ The active terminal decides, never another one: attached in its place, the output of an older command in another
   *  terminal reads as the one the person just ran — the notice names that terminal instead. */
  attachment(): { chip: string; content: string } | { notice: string } {
    const terminal = vscode.window.activeTerminal;
    if (!terminal) {
      return { notice: t('No terminal is open.') };
    }
    if (!this.followed.has(terminal)) {
      this.followed.add(terminal);
      return terminal.shellIntegration
        ? { notice: t("Inferpal follows this terminal's commands from now on: run the command again, then use @terminal.") }
        : { notice: shellIntegrationOff() };
    }
    // A newer command still running: its predecessor is not what the person asks about.
    if ((this.running.get(terminal) ?? 0) > 0) {
      return { notice: t('A command is still running in this terminal: use @terminal again once it has finished.') };
    }
    const command = this.last.get(terminal);
    if (!command) {
      if (!terminal.shellIntegration) {
        return { notice: shellIntegrationOff() };
      }
      const other = [...this.last.entries()].filter(([t2]) => t2 !== terminal).sort((a, b) => b[1].at - a[1].at)[0]?.[0];
      return other
        ? { notice: t('No command has finished in this terminal; the last one finished in “{0}”: switch to it, then use @terminal.', other.name) }
        : { notice: t('No command has finished in this terminal since Inferpal began following it: run it again, then use @terminal.') };
    }
    const short = command.command.length > 40 ? command.command.slice(0, 39) + '…' : command.command;
    const chip = short ? `⌨ terminal: ${short}` : '⌨ terminal';
    return {
      // The cut said to the person too, on the chip: the model reads it in the marker.
      chip: command.cut ? t('{0} (its end only)', chip) : chip,
      content: terminalAttachment(command.command, command.cwd, command.output, command.cut, command.exitCode),
    };
  }
}

function shellIntegrationOff(): string {
  return t("This terminal's shell integration is off: VS Code cannot tell where a command's output starts and ends. It is on by default for bash, zsh, fish and PowerShell (terminal.integrated.shellIntegration.enabled).");
}
