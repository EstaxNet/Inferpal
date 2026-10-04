// The webview's icon set: inline stroke SVG, drawn in the current text color. The same drawings as the design
// mockups, so both editors can share one visual language. No font, no image: nothing for the CSP to allow, and an
// icon scales with the text around it.
//
// ⚠ An icon-only button needs a label for screen readers: `iconButton` sets `aria-label` from its title, so a button
// built here is never announced as "button" alone.

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Each icon: the children of a 24×24 viewBox — `p` a path, `c` a circle (cx cy r), `r` a rect (x y w h rx). */
const DRAWINGS = {
  plus: [['p', 'M12 5v14M5 12h14']],
  close: [['p', 'M6 6l12 12M18 6L6 18']],
  search: [['c', '11 11 7'], ['p', 'M20 20l-4-4']],
  retry: [['p', 'M20 11a8 8 0 1 0-2.3 5.7'], ['p', 'M20 4v7h-7']],
  send: [['p', 'M12 19V5M5 12l7-7 7 7']],
  stop: [['r', '6 6 12 12 2.5']],
  copy: [['r', '9 9 11 11 2'], ['p', 'M5 15V5a2 2 0 0 1 2-2h10']],
  regenerate: [['p', 'M4 12a8 8 0 0 1 13.7-5.7L20 9'], ['p', 'M20 3v6h-6'], ['p', 'M20 12a8 8 0 0 1-13.7 5.7L4 15'], ['p', 'M4 21v-6h6']],
  chevronRight: [['p', 'M9 6l6 6-6 6']],
  chevronDown: [['p', 'M6 9l6 6 6-6']],
  tool: [['p', 'M14.7 6.3a4 4 0 0 0-5.4 5.2L4 16.8V20h3.2l5.3-5.3a4 4 0 0 0 5.2-5.4l-2.6 2.6-2.4-.6-.6-2.4z']],
  check: [['p', 'M5 12l5 5L20 7']],
  failed: [['c', '12 12 9'], ['p', 'M9 9l6 6M15 9l-6 6']],
  pending: [['c', '12 12 8']],
  running: [['c', '12 12 8'], ['p', 'M12 8v4l2.5 1.5']],
  goal: [['c', '12 12 8'], ['c', '12 12 4']],
  pin: [['p', 'M9 4h6l-1 6 3 3H7l3-3zM12 13v7']],
  pause: [['r', '7 5 3 14 1'], ['r', '14 5 3 14 1']],
  play: [['p', 'M8 5v14l11-7z']],
  external: [['p', 'M14 4h6v6M20 4l-9 9M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5']],
  info: [['c', '12 12 9'], ['p', 'M12 11v6M12 7.5v.5']],
  warning: [['p', 'M12 4l9 16H3z'], ['p', 'M12 10v4M12 17v.5']],
  xray: [['r', '4 4 16 16 3'], ['p', 'M4 10h16M10 4v16']],
  explain: [['c', '12 12 9'], ['p', 'M12 11v6M12 7.5v.5']],
  bug: [['r', '7 8 10 12 5'], ['p', 'M12 8v12M4 13h3M17 13h3M5 8l3 2M19 8l-3 2M9 4l1.5 2M15 4l-1.5 2']],
  test: [['p', 'M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6A2 2 0 0 0 19 18l-5-9V3']],
  help: [['c', '12 12 9'], ['p', 'M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .8-1 1.5v.7M12 17v.5']],
  edit: [['p', 'M4 20h4L19 9l-4-4L4 16z'], ['p', 'M13.5 6.5l4 4']],
  trash: [['p', 'M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3']],
  folder: [['p', 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z']],
  file: [['p', 'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z'], ['p', 'M14 3v5h5']],
  list: [['p', 'M4 6h16M4 12h16M4 18h10']],
  history: [['c', '12 12 9'], ['p', 'M12 7v5l3 2']],
  more: [['p', 'M5 12h.01M12 12h.01M19 12h.01']],
  attach: [['p', 'M20 11.5l-8.2 8.2a5 5 0 0 1-7-7L13 4.5a3.4 3.4 0 0 1 4.8 4.8l-8.1 8.1a1.7 1.7 0 0 1-2.4-2.4l7.4-7.4']],
} satisfies Record<string, ReadonlyArray<readonly [string, string]>>;

export type IconName = keyof typeof DRAWINGS;

/** The icon as an SVG element, `size` px square, decorative (hidden from screen readers). */
export function icon(name: IconName, size = 16): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('class', 'icon icon-' + name);
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('fill', name === 'play' ? 'currentColor' : 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '2');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  for (const [kind, data] of DRAWINGS[name]) {
    const values = data.split(' ');
    const child = document.createElementNS(SVG_NS, kind === 'p' ? 'path' : kind === 'c' ? 'circle' : 'rect');
    if (kind === 'p') {
      child.setAttribute('d', data);
    } else if (kind === 'c') {
      child.setAttribute('cx', values[0]);
      child.setAttribute('cy', values[1]);
      child.setAttribute('r', values[2]);
    } else {
      child.setAttribute('x', values[0]);
      child.setAttribute('y', values[1]);
      child.setAttribute('width', values[2]);
      child.setAttribute('height', values[3]);
      child.setAttribute('rx', values[4] ?? '0');
    }
    svg.appendChild(child);
  }
  return svg;
}

/** Puts `name` in `el` (replacing what it held), followed by `label` when there is one. */
export function setIcon(el: HTMLElement, name: IconName, label?: string, size = 16): void {
  el.textContent = '';
  el.classList.add('with-icon');
  el.appendChild(icon(name, size));
  if (label) {
    const span = document.createElement('span');
    span.textContent = label;
    el.appendChild(span);
  }
}

/** An icon-only button: its title is also its accessible name. */
export function iconButton(name: IconName, title: string, size = 16): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.className = 'icon-button with-icon';
  btn.title = title;
  btn.setAttribute('aria-label', title);
  btn.appendChild(icon(name, size));
  return btn;
}
