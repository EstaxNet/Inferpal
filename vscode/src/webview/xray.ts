// Context X-Ray panel (interactive /xray V2): ephemeral overlay above the transcript.
// State lives host-side — this only renders the pushed panel model; expansion state
// survives toggle-driven re-renders.
import type { XRayPanel } from '../protocol';
import { t } from './l10n';

const xrayEl = document.createElement('div');
xrayEl.id = 'xray';
xrayEl.hidden = true;
document.body.appendChild(xrayEl);
const expanded = new Set<string>();

type XraySink = (msg: { type: 'xrayToggle'; id: string; enabled: boolean } | { type: 'copyText'; text: string }) => void;
let post: XraySink = () => undefined;
export function setXraySink(sink: XraySink): void {
  post = sink;
}

export function renderXray(panel: XRayPanel): void {
  xrayEl.textContent = '';

  const header = document.createElement('div');
  header.className = 'xray-header';
  const title = document.createElement('span');
  title.className = 'xray-title';
  title.textContent = t('xrayTitle', panel.totalTokens.toLocaleString());
  const copyBtn = document.createElement('button');
  copyBtn.textContent = t('xrayCopyPrompt');
  copyBtn.title = t('xrayCopyPromptTitle');
  copyBtn.addEventListener('click', () => post({ type: 'copyText', text: panel.rawPrompt }));
  const closeBtn = document.createElement('button');
  closeBtn.textContent = '✕';
  closeBtn.addEventListener('click', () => { xrayEl.hidden = true; });
  header.appendChild(title);
  header.appendChild(copyBtn);
  header.appendChild(closeBtn);
  xrayEl.appendChild(header);

  const list = document.createElement('div');
  list.className = 'xray-list';
  for (const s of panel.sections) {
    const row = document.createElement('div');
    row.className = 'xray-row' + (s.enabled ? '' : ' off');

    const line = document.createElement('div');
    line.className = 'xray-line';
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.checked = s.enabled;
    check.disabled = !s.canToggle;
    check.title = s.canToggle ? t('xrayInclude') : '';
    check.addEventListener('change', () => post({ type: 'xrayToggle', id: s.id, enabled: check.checked }));
    const label = document.createElement('span');
    label.className = 'xray-label';
    label.textContent = s.label;
    const tokens = document.createElement('span');
    tokens.className = 'xray-tokens';
    tokens.textContent = '~' + s.tokens.toLocaleString();
    const chevron = document.createElement('button');
    chevron.className = 'xray-chevron';
    chevron.textContent = expanded.has(s.id) ? '▾' : '▸';
    const toggleContent = () => {
      if (expanded.has(s.id)) { expanded.delete(s.id); } else { expanded.add(s.id); }
      renderXray(panel);
    };
    chevron.addEventListener('click', toggleContent);
    label.addEventListener('click', toggleContent);
    line.appendChild(check);
    line.appendChild(label);
    line.appendChild(tokens);
    line.appendChild(chevron);
    row.appendChild(line);

    const bar = document.createElement('div');
    bar.className = 'xray-bar';
    const fill = document.createElement('div');
    fill.className = 'xray-fill';
    fill.style.width = Math.max(0, Math.min(100, s.percent)) + '%';
    bar.appendChild(fill);
    row.appendChild(bar);

    if (expanded.has(s.id)) {
      const pre = document.createElement('pre');
      pre.className = 'xray-content';
      pre.textContent = s.content;
      row.appendChild(pre);
    }
    list.appendChild(row);
  }
  xrayEl.appendChild(list);

  const footer = document.createElement('div');
  footer.className = 'xray-footer';
  if (panel.overheadWarning) {
    const warn = document.createElement('div');
    warn.className = 'xray-warning';
    warn.textContent = t('xrayWarning');
    footer.appendChild(warn);
  }
  const info = document.createElement('div');
  info.textContent = t('xrayHistory', panel.historyTokens.toLocaleString())
    + (panel.contextWindow > 0 ? ' · ' + t('xrayWindow', panel.fillPercent.toFixed(0)) : '');
  footer.appendChild(info);
  const hint = document.createElement('div');
  hint.textContent = t('xrayHint');
  footer.appendChild(hint);
  xrayEl.appendChild(footer);

  xrayEl.hidden = false;
}
