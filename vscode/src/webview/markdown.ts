// Markdown rendering for the chat webview: markdown-it bundled by esbuild (no CDN — the
// CSP stays nonce-only), html disabled so raw model HTML is neutralized. Parity target is
// the VS renderer (Markdig blocks: headings, lists, tables, separators, code fences).
import MarkdownIt from 'markdown-it';
import { t } from './l10n';

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
});

/** Reasoning tags (Qwen3/DeepSeek) are stripped before rendering, like the VS
 * MarkdownParser. An unclosed trailing tag (mid-stream) is stripped too. */
export function stripThinkTags(text: string): string {
  return text.replace(/<think>[\s\S]*?<\/think>/g, '').replace(/<think>[\s\S]*$/, '');
}

/** Renders markdown into `target` and decorates code blocks with a copy button. */
export function renderMarkdownInto(target: HTMLElement, text: string): void {
  target.innerHTML = md.render(stripThinkTags(text));
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
  copy.addEventListener('click', () => {
    const code = pre.querySelector('code');
    dispatchCopy(code ? code.textContent ?? '' : pre.textContent ?? '');
    copy.textContent = t('copied');
    setTimeout(() => { copy.textContent = t('copy'); }, 1500);
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
