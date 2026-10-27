// What a terminal command left, as the model reads it: its output without the terminal's escape sequences, each line at
// the last state a carriage return left it in (a progress bar rewrites one line a hundred times), and — a command's
// verdict being at its end — bounded by its END, the cut said. Pure, as attachmentCap.ts: no editor API here.

/** CSI sequences (colours, cursor moves), OSC sequences (titles, links, the shell integration's own marks) ended by BEL
 *  or ST, the other string sequences (DCS, SOS, PM, APC) ended by ST, and every other escape — `ESC ( B`, `ESC 7`,
 *  `ESC =`. Each begins with ESC: none can take real text. */
const ESCAPES = /\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[PX^_][^\x1b]*\x1b\\|\x1b[ -/]*[0-~]/g;

/** A cursor placed on a line (CUP, HVP, VPA): what follows is on another line — the Windows console repaints this way. */
const CURSOR_LINE = /\x1b\[[0-9;]*[Hfd]/g;

/** Control characters a terminal leaves behind (a bell, a backspace), tab and line feed kept. */
const CONTROLS = /[\x00-\x08\x0b-\x1f\x7f]/g;

/** The output as text: escapes out, each line at the last state a carriage return left it in. */
export function readableOutput(raw: string): string {
  return raw
    .replace(CURSOR_LINE, '\n')
    .replace(ESCAPES, '')
    .replace(/\r\n/g, '\n')
    .split('\n')
    // A line written \r\r\n (a text-mode program on Windows) ends in a carriage return that rewrites nothing.
    .map((line) => line.replace(/\r+$/, ''))
    .map((line) => line.slice(line.lastIndexOf('\r') + 1))
    .join('\n')
    .replace(CONTROLS, '');
}

/** A command's output as it streams, kept by its end: at most `max` characters, and how many before them were dropped. */
export class TailBuffer {
  private text = '';
  private droppedCount = 0;

  constructor(private readonly max: number) {}

  append(chunk: string): void {
    this.text += chunk;
    // Trimmed by halves: a chunk at a time would copy the whole buffer for every chunk of a long output.
    if (this.text.length > this.max * 2) {
      this.trim();
    }
  }

  /** The characters kept, the last `max` of what was written. */
  kept(): string {
    this.trim();
    return this.text;
  }

  /** How many characters were written before the ones kept. */
  dropped(): number {
    this.trim();
    return this.droppedCount;
  }

  private trim(): void {
    const over = this.text.length - this.max;
    if (over > 0) {
      this.text = this.text.slice(over);
      this.droppedCount += over;
    }
  }
}

/** What `@terminal` attaches — the command, its folder, its output, its exit code — for the model, in English.
 *  @param cut Whether the start of the output was dropped: its first line, likely cut in the middle of a line or of an
 *  escape sequence, goes too. No count is said — what was dropped holds escapes and repainted progress lines, not as
 *  many characters of output. */
export function terminalAttachment(command: string, cwd: string | undefined, output: string, cut: boolean,
                                   exitCode: number | undefined): string {
  const lines = [`$ ${command || '(the command line was not reported)'}` + (cwd ? `    (in ${cwd})` : '')];
  if (cut) {
    lines.push('…(the start of the output is cut: only its end is attached)');
    output = output.slice(output.indexOf('\n') + 1);
  }
  lines.push(output.trimEnd() || '(no output)');
  lines.push(exitCode === undefined ? '[exit code: not reported by the shell]' : `[exit code: ${exitCode}]`);
  return lines.join('\n');
}
