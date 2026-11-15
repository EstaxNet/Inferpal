// A Mermaid diagram the model wrote, drawn in the chat. Mermaid 12.2.0 is shipped with the extension
// (third-party/mermaid) and loaded on the first diagram, under the page's nonce; nothing is fetched. A diagram is drawn as
// an SVG image (`data:`): an image runs no script and loads nothing, whatever its labels hold. A fence not closed yet (the
// answer still streaming) stays code; a diagram Mermaid cannot read says why, its code shown.
import { t } from './l10n';
import { setIcon } from './icons';

type MermaidApi = {
  initialize(config: Record<string, unknown>): void;
  render(id: string, code: string): Promise<{ svg: string }>;
};
type Drawing = { url?: string; svg?: string; error?: string; theme?: string };

let loading: Promise<MermaidApi> | null = null;
let themeInUse = '';
let seq = 0;
/**
 * Diagrams by theme and code, the drawing in flight included: a streamed answer is rendered again at every frame, and
 * each frame asked Mermaid for the same diagram again until the first drawing came back.
 */
const drawn = new Map<string, Promise<Drawing>>();
/** The drawings that came back, for a frame that redraws the block: shown at once, nothing announced again. */
const done = new Map<string, Drawing>();
/** The diagrams the person switched to Code: kept across the frames of a streamed answer. */
const showingCode = new Set<string>();

/** Mermaid's theme for VS Code's. ⚠ Light high contrast carries `vscode-high-contrast` too: checked first. */
function theme(): string {
  const c = document.body.classList;
  if (c.contains('vscode-high-contrast-light') || c.contains('vscode-light')) {
    return 'default';
  }
  return 'dark';
}

function load(): Promise<MermaidApi> {
  if (!loading) {
    loading = new Promise((resolve, reject) => {
      const src = document.body.dataset.mermaid;
      if (!src) {
        reject(new Error('no Mermaid in this page'));
        return;
      }
      const script = document.createElement('script');
      script.src = src;
      script.nonce = (document.querySelector('script[nonce]') as HTMLScriptElement | null)?.nonce ?? '';
      script.onload = () => resolve((window as unknown as { mermaid: MermaidApi }).mermaid);
      script.onerror = () => reject(new Error('Mermaid did not load'));
      document.head.appendChild(script);
    });
  }
  return loading;
}

function draw(code: string): Promise<Drawing> {
  const key = theme() + '\n' + code;
  let pending = drawn.get(key);
  if (!pending) {
    if (drawn.size > 64) {
      drawn.clear();
      done.clear();
    }
    pending = render(code, theme()).then((d) => {
      done.set(key, d);
      return d;
    });
    drawn.set(key, pending);
  }
  return pending;
}

async function render(code: string, th: string): Promise<Drawing> {
  let id = '';
  try {
    const mermaid = await load();
    if (themeInUse !== th) {
      const natural = { useMaxWidth: false };
      // ⚠ One font for measuring and for drawing: Mermaid measures its labels in this page, whose policy keeps its own
      // styles out (the page's font measures), then the image draws them in the font the SVG names — a label measured
      // narrow was cut ("ye" for "yes"). The page's computed font, named in the SVG, is the same on both sides.
      const body = getComputedStyle(document.body);
      // suppressErrorRendering: a diagram that does not read drew Mermaid's own error panel into the page.
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', suppressErrorRendering: true, theme: th,
        fontFamily: body.fontFamily,
        themeVariables: { fontFamily: body.fontFamily, fontSize: body.fontSize },
        flowchart: natural, sequence: natural, class: natural, state: natural, er: natural, gantt: natural, pie: natural,
        journey: natural });
      themeInUse = th;
    }
    id = `inferpal-mmd-${++seq}`;
    const { svg } = await mermaid.render(id, code);
    const bytes = new TextEncoder().encode(svg);
    let binary = '';
    for (const b of bytes) {
      binary += String.fromCharCode(b);
    }
    return { url: 'data:image/svg+xml;base64,' + btoa(binary), svg, theme: th };
  } catch (err) {
    // Mermaid's own reason, its first line: what to tell the model to fix.
    return { error: String((err as Error)?.message ?? err).split('\n')[0].replace(/[\s:]+$/, '').slice(0, 200) + '.' };
  } finally {
    // What Mermaid measured in, left in the page when it fails.
    for (const left of [id, 'd' + id]) {
      if (id) {
        document.getElementById(left)?.remove();
      }
    }
  }
}

/** What the extension does for a diagram: open it larger. */
let openSink: (svg: string) => void = () => undefined;
export function setDiagramOpenSink(sink: (svg: string) => void): void {
  openSink = sink;
}

/**
 * The code block of a closed ```mermaid fence, decorated: a bar (Diagram, Code, copy, open larger) over the drawing, or
 * over the code with Mermaid's reason when it does not read.
 */
export function decorateMermaid(wrapper: HTMLElement, pre: HTMLElement, code: string, copy: (text: string) => void): void {
  watchTheme();
  wrapper.classList.add('mermaid-block');
  const bar = document.createElement('div');
  bar.className = 'mermaid-bar';
  const label = document.createElement('span');
  label.className = 'mermaid-label';
  label.textContent = t('mermaidLabel');
  const group = document.createElement('div');
  group.className = 'mermaid-switch';
  group.setAttribute('role', 'group');
  group.setAttribute('aria-label', t('mermaidSwitch'));
  const showDiagram = document.createElement('button');
  showDiagram.textContent = t('mermaidDiagram');
  const showCode = document.createElement('button');
  showCode.textContent = t('mermaidCode');
  group.append(showDiagram, showCode);
  const copyButton = document.createElement('button');
  copyButton.className = 'mermaid-icon';
  setIcon(copyButton, 'copy', undefined, 14);
  copyButton.title = t('mermaidCopy');
  copyButton.setAttribute('aria-label', t('mermaidCopy'));
  copyButton.addEventListener('click', () => copy(code));
  const openButton = document.createElement('button');
  openButton.className = 'mermaid-icon';
  setIcon(openButton, 'external', undefined, 14);
  openButton.title = t('mermaidOpen');
  openButton.setAttribute('aria-label', t('mermaidOpen'));
  openButton.disabled = true;
  bar.append(label, group, copyButton, openButton);

  const canvas = document.createElement('div');
  canvas.className = 'mermaid-canvas';
  wrapper.insertBefore(bar, pre);
  wrapper.insertBefore(canvas, pre);

  const select = (diagram: boolean) => {
    showDiagram.setAttribute('aria-pressed', String(diagram));
    showCode.setAttribute('aria-pressed', String(!diagram));
    canvas.hidden = !diagram;
    pre.hidden = diagram;
    if (diagram) {
      showingCode.delete(code);
    } else {
      showingCode.add(code);
    }
  };
  showDiagram.addEventListener('click', () => select(true));
  showCode.addEventListener('click', () => select(false));
  select(!showingCode.has(code));

  const show = (result: Drawing) => {
    canvas.textContent = '';
    canvas.removeAttribute('role');
    if (result.url) {
      const img = document.createElement('img');
      img.src = result.url;
      img.alt = t('mermaidAlt');
      img.dataset.mermaidCode = code;
      // The theme it was drawn in: one switched while it drew is drawn again by the watcher, which sees it arrive.
      img.dataset.mermaidTheme = result.theme ?? '';
      img.dataset.mermaidSvg = result.svg ?? '';
      canvas.appendChild(img);
      openButton.disabled = false;
      openButton.addEventListener('click', () => openSink(img.dataset.mermaidSvg ?? ''));
      return;
    }
    // Never an empty picture: the reason, and the code.
    const note = document.createElement('div');
    note.className = 'mermaid-broken';
    note.setAttribute('role', 'status');
    const title = document.createElement('span');
    title.className = 'mermaid-broken-title';
    setIcon(title, 'warning', t('mermaidBroken'), 14);
    const why = document.createElement('span');
    why.className = 'mermaid-broken-why';
    why.textContent = t('mermaidWhy', result.error ?? '');
    note.append(title, why);
    wrapper.insertBefore(note, bar.nextSibling);
    canvas.remove();
    group.remove();
    pre.hidden = false;
  };
  // Already drawn (a frame of a streamed answer that redraws the block): shown at once, nothing announced again.
  const known = done.get(theme() + '\n' + code);
  if (known) {
    show(known);
    return;
  }
  canvas.setAttribute('role', 'status');
  canvas.textContent = t('mermaidDrawing');
  void draw(code).then(show);
}

/**
 * VS Code switched between dark and light: every drawn diagram is drawn again in the new theme's colours — watched from
 * the first diagram on, so a switch during the first drawing is not missed (a drawing whose theme is no longer the page's
 * is drawn again).
 */
let watching = false;
function watchTheme(): void {
  if (watching) {
    return;
  }
  watching = true;
  new MutationObserver(() => {
    const th = theme();
    for (const img of [...document.querySelectorAll<HTMLImageElement>('img[data-mermaid-code]')]) {
      if (img.dataset.mermaidTheme === th) {
        continue;
      }
      img.dataset.mermaidTheme = th;
      void draw(img.dataset.mermaidCode ?? '').then((again) => {
        if (again.url) {
          img.src = again.url;
          img.dataset.mermaidSvg = again.svg ?? '';
        }
      });
    }
  }).observe(document.body, { attributes: true, attributeFilter: ['class'], subtree: true, childList: true });
}

/**
 * Whether the fence spanning `map` (markdown-it's lines) is closed: its last line closes it — the opener's character, at
 * least as many, after any blockquote or list prefix. An answer still streaming has its last fence open, and markdown-it
 * reads it to the end of the text (a ```` fence whose last line so far is ``` is still open).
 */
export function fenceClosed(map: [number, number] | null, lines: string[], markup: string): boolean {
  if (!map || map[1] - map[0] < 2 || markup.length < 3) {
    return false;
  }
  const last = (lines[map[1] - 1] ?? '').replace(/^[\s>]*/, '').trim();
  const run = last.match(/^(`+|~+)$/)?.[1] ?? '';
  return run.length >= markup.length && run[0] === markup[0];
}
