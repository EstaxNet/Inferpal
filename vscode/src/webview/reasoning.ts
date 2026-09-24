// Reasoning removal for the chat webview — the same rule as the Core's MarkdownParser.StripThinkTags,
// which the VS window renders with.

const THINK_OPEN = '<think>';
const THINK_CLOSE = '</think>';

/**
 * Removes the model's reasoning blocks (`<think>…</think>`, whatever their case).
 *
 * Reasoning is text the model EMITS — at the head of a turn, or between the turns of an agent run
 * streamed into one message; a reply stopped mid-reasoning ends inside an unclosed tag, and that tail is
 * reasoning too. A tag inside a fenced block or a code span is something the answer SHOWS: asked how to
 * strip Qwen3's reasoning, a model answers with the regex, and removing every tag it found deleted half
 * that answer and left a broken fence — or, for an opening tag named in backticks, everything after it.
 * So code is copied untouched.
 *
 * ⚠ The content of a block is skipped whole, never scanned: reasoning writes code too, and a fence it
 * opens and never closes must not turn the answer that follows into a code block.
 */
export function stripThinkTags(text: string): string {
  if (!text || indexOfTag(text, THINK_OPEN, 0) < 0) {
    return text ?? '';
  }

  // Kept text as slices: appending to one string and asking it `endsWith` flattens it at every step —
  // seconds on a long code-heavy answer, and this runs on every streamed token.
  const kept: string[] = [];
  let lastKept = '';
  const keep = (from: number, to: number): void => {
    if (to > from) {
      kept.push(text.slice(from, to));
      lastKept = text[to - 1];
    }
  };

  let fenceChar = '';
  let fenceLength = 0; // > 0 while inside a fenced block
  let i = 0;
  while (i < text.length) {
    // Line structure follows what is KEPT: a block removed at the head of a line leaves that line's start.
    const atLineStart = kept.length === 0 || lastKept === '\n';
    const fence = atLineStart ? readFence(text, i) : undefined;
    if (fence && (fenceLength === 0 || (fence.char === fenceChar && fence.length >= fenceLength && fence.bare))) {
      if (fenceLength === 0) {
        fenceChar = fence.char;
        fenceLength = fence.length;
      } else {
        fenceLength = 0;
      }
      const end = lineEnd(text, i);
      keep(i, end);
      i = end;
      continue;
    }
    if (fenceLength > 0) {
      const end = lineEnd(text, i);
      keep(i, end);
      i = end;
      continue;
    }

    if (text[i] === '`') {
      const run = runLength(text, i, '`');
      const closer = spanCloser(text, i + run, run);
      const end = closer < 0 ? i + run : closer + run; // an unmatched run is literal text
      keep(i, end);
      i = end;
      continue;
    }

    if (tagAt(text, THINK_OPEN, i)) {
      const close = indexOfTag(text, THINK_CLOSE, i + THINK_OPEN.length);
      if (close < 0) {
        break;
      }
      i = close + THINK_CLOSE.length;
      continue;
    }

    // Plain text up to the next thing that can start a span or a tag, or to the end of the line.
    let end = i + 1;
    while (end < text.length && text[end] !== '`' && text[end] !== '<' && text[end - 1] !== '\n') {
      end++;
    }
    keep(i, end);
    i = end;
  }
  return kept.join('');
}

// Case-insensitive search for an ASCII tag. Not through toLowerCase(): lower-casing can change the length of
// the text before the tag, and the indexes found would no longer point into the original.
function indexOfTag(s: string, tag: string, from: number): number {
  for (let i = from; i <= s.length - tag.length; i++) {
    if (tagAt(s, tag, i)) {
      return i;
    }
  }
  return -1;
}

function tagAt(s: string, tag: string, i: number): boolean {
  return s[i] === '<' && s.slice(i, i + tag.length).toLowerCase() === tag;
}

// A fence line: up to three spaces, then three or more backticks or tildes. `bare` = nothing after the
// run, which a closing fence requires. A backtick run followed by another backtick on the same line is
// an inline span (```x```), not a fence.
function readFence(s: string, lineStart: number): { char: string; length: number; bare: boolean } | undefined {
  let i = lineStart;
  let indent = 0;
  while (i < s.length && s[i] === ' ' && indent < 3) {
    i++;
    indent++;
  }
  if (i >= s.length || (s[i] !== '`' && s[i] !== '~')) {
    return undefined;
  }
  const char = s[i];
  const length = runLength(s, i, char);
  if (length < 3) {
    return undefined;
  }
  const rest = s.slice(i + length, lineEnd(s, i + length));
  if (char === '`' && rest.includes('`')) {
    return undefined;
  }
  return { char, length, bare: rest.trim().length === 0 };
}

function runLength(s: string, start: number, char: string): number {
  let end = start;
  while (end < s.length && s[end] === char) {
    end++;
  }
  return end - start;
}

// Index of the backtick run of exactly `run` characters that closes a code span, or -1. A span does not
// cross a blank line: past a paragraph break an unmatched run is literal text.
function spanCloser(s: string, from: number, run: number): number {
  let i = from;
  while (i < s.length) {
    if (s[i] === '`') {
      const length = runLength(s, i, '`');
      if (length === run) {
        return i;
      }
      i += length;
      continue;
    }
    if (s[i] === '\n') {
      let j = i + 1;
      while (j < s.length && (s[j] === ' ' || s[j] === '\t' || s[j] === '\r')) {
        j++;
      }
      if (j >= s.length || s[j] === '\n') {
        return -1;
      }
    }
    i++;
  }
  return -1;
}

// End of the line starting at or containing `start`, past its line break.
function lineEnd(s: string, start: number): number {
  const eol = s.indexOf('\n', start);
  return eol < 0 ? s.length : eol + 1;
}
