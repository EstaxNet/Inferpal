// Markdown rendering for the chat webview: markdown-it bundled by esbuild (no CDN — the
// CSP stays nonce-only), html disabled so raw model HTML is neutralized. Parity target is
// the VS renderer (Markdig blocks: headings, lists, tables, separators, code fences).
import MarkdownIt from 'markdown-it';
import { t } from './l10n';
import { stripThinkTags } from './reasoning';

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
});

/**
 * A link the model wrote stays clickable only for a web address — as in Visual Studio (`MarkdownParser.WebUrl`).
 * ⚠ markdown-it's own check refuses javascript:, vbscript:, file: and data: only: a `vscode://…` link, `ms-settings:`
 * or any scheme a program registered stayed clickable, and its click launched that program.
 */
export function isWebLink(url: string): boolean {
  return /^https?:\/\/[^\s]/i.test(url.trim());
}

/** Any other link keeps its words and loses its target, as in Visual Studio. */
function disarmLinks(target: HTMLElement): void {
  for (const a of [...target.querySelectorAll('a')]) {
    if (!isWebLink(a.getAttribute('href') ?? '')) {
      a.replaceWith(...a.childNodes);
    }
  }
}

// Reasoning tags (Qwen3/DeepSeek) are stripped before rendering, like the VS MarkdownParser: html is
// disabled, so a tag left in place shows the chain of thought as text.
export { stripThinkTags };

/** Renders markdown into `target` and decorates code blocks with a copy button.
 *  ⚠ Only an ANSWER loses its reasoning: a question may quote the tag, and shown stripped it read cut. */
export function renderMarkdownInto(target: HTMLElement, text: string, stripReasoning = true): void {
  target.innerHTML = stripReasoning ? md.render(stripThinkTags(text)) : md.render(text);
  disarmLinks(target);
  for (const pre of target.querySelectorAll('pre')) {
    decorateCodeBlock(pre as HTMLElement);
  }
}

function decorateCodeBlock(pre: HTMLElement): void {
  const wrapper = document.createElement('div');
  wrapper.className = 'codeblock';
  pre.replaceWith(wrapper);
  wrapper.appendChild(pre);

  const copy = document.createElement('button');
  copy.className = 'codeblock-copy';
  copy.textContent = t('copy');
  copy.title = t('copy');
  // ⚠ Announces nothing, as in Visual Studio: the copy is the extension's, and a "Copied!" said before it is known would
  // have the person paste what the clipboard held before. A copy that fails is said by the extension.
  copy.addEventListener('click', () => {
    const code = pre.querySelector('code');
    dispatchCopy(code ? code.textContent ?? '' : pre.textContent ?? '');
  });
  wrapper.appendChild(copy);
}

// The clipboard write goes through the extension host (navigator.clipboard is not
// reliably available under the webview CSP). main.ts registers the actual sender.
let copySink: (text: string) => void = () => undefined;
export function setCopySink(sink: (text: string) => void): void {
  copySink = sink;
}
function dispatchCopy(text: string): void {
  copySink(text);
}
