// What an attachment sends of a file — and, when that is not the whole file, says so to both readers: the model reads
// the counts in the marker, the person reads them under the question. A cut that nobody names is a file that ends
// early: "is X defined in this file?" is then answered "no" about code that sits past the cut.

export interface CappedText {
  /** What is sent: the text, or its first lines followed by the marker. */
  text: string;
  /** Characters of the original that are sent. */
  shown: number;
  /** Characters of the original. */
  total: number;
  cut: boolean;
}

/** The text within `max` characters, cut on a line end when one is near, the cut said with both counts. */
export function capAttachment(text: string, max: number): CappedText {
  if (text.length <= max) {
    return { text, shown: text.length, total: text.length, cut: false };
  }
  const lineEnd = text.lastIndexOf('\n', max);
  const shown = lineEnd > max / 2 ? lineEnd : max;
  // For the model, in English like every structural marker; it names the tool that reads the rest.
  const marker = `\n…(truncated: the first ${shown.toLocaleString('en-US')} of ${text.length.toLocaleString('en-US')}`
    + ' characters are attached; read_file reads the rest)';
  return { text: text.slice(0, shown) + marker, shown, total: text.length, cut: true };
}
