// Inferpal Settings webview: mirrors the Visual Studio settings window — same tabs, same
// sections, same labels/hints (served localized by the host from the shared .resx via
// `settings/strings`), ⓘ tooltips, compact numeric fields with unit suffixes, the
// "distinct model per role" and "advanced settings" reveal toggles, and Test / refresh
// buttons. The config JSON is mutated in place and saved WHOLE (config/update replaces
// the config — absent fields would reset).
import { fill, t } from './l10n';

const vscode = acquireVsCodeApi();

// Localized resources pushed by the extension (resx resource name → translated string).
let R: Record<string, string> = {};
const res = (key: string): string => R[key] ?? key;

// The fields the last save could not read: decided when sending, rendered when the host answers
// (`saveDone`), which is the only moment we know the save actually happened.
let lastIgnored: string[] = [];

/** A field label as it is quoted inside a sentence: without its trailing colon. Labels are
 *  written to sit in front of a box, and quoted as-is inside an enumeration they read "Context
 *  window :, Results per query :". Same gesture as `SettingsFallback.LabelForSentence` in the
 *  Core, on the same labels. */
const labelForSentence = (label: string): string => label.replace(/[\s\u00A0\u202F:\uFF1A]+$/, '');

/**
 * The form is declared once in the Core (`SettingsSchema`) and served by the host over
 * `settings/schema`; these are the wire shapes. Adding a setting no longer means editing a table
 * here — it means adding it to the Core schema, where a test checks it against InferpalConfig and
 * against the .resx.
 */
interface Option { value: string; text: string }

interface Field {
  /** camelCase key in the config JSON. */
  key: string;
  kind: 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';
  /** resx resource names for the label and the ⓘ tooltip. */
  label: string;
  hint?: string | null;
  /** Unit suffix rendered after compact numeric fields (literal, like VS). */
  unit?: string | null;
  /** UI-only reveal group: 'roles' (distinct model per role) or 'advanced' (behavior). */
  gate?: string | null;
  /** Companion button: 'test' (connection check) or 'refreshModels'. */
  button?: string | null;
  options?: Option[] | null;
}

interface Section {
  title: string;
  fields: Field[];
  toggleGate?: string | null;
  toggleLabel?: string | null;
  toggleHint?: string | null;
}

interface Tab { key: string; title: string; sections: Section[] }

interface Schema { tabs: Tab[]; headerFields: Field[] }

/** Served by the host at init; empty until then. */
let SCHEMA: Schema = { tabs: [], headerFields: [] };

// Fixed like the VS list — language names are deliberately never localized.
const LANGUAGES = [
  { value: '', text: 'Auto' },
  { value: 'en', text: 'English' }, { value: 'fr', text: 'Français' },
  { value: 'de', text: 'Deutsch' }, { value: 'es', text: 'Español' },
  { value: 'it', text: 'Italiano' }, { value: 'ru', text: 'Русский' },
  { value: 'ja', text: '日本語' }, { value: 'ko', text: '한국어' },
  { value: 'pl', text: 'Polski' }, { value: 'zh-CN', text: '中文(简体)' },
];

const app = document.getElementById('app')!;
let config: Record<string, unknown> = {};
let models: string[] = [];
const inputs = new Map<string, HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>();
let statusEl: HTMLElement | null = null;
let testStatusEl: HTMLElement | null = null;
const gateOn: Record<'roles' | 'advanced', boolean> = { roles: false, advanced: false };

function label(field: Field): string {
  if (field.label === '__inlineDiffPreview') {
    return t('Inline diff preview for code actions');
  }
  return res(field.label);
}

function infoIcon(hintKey?: string | null): HTMLElement | null {
  if (!hintKey) {
    return null;
  }
  const hint = R[hintKey];
  if (!hint) {
    return null;
  }
  const icon = document.createElement('span');
  icon.className = 'info';
  icon.textContent = 'ⓘ';
  icon.title = hint;
  return icon;
}

function applyGates(): void {
  for (const el of app.querySelectorAll<HTMLElement>('[data-gate]')) {
    el.hidden = !gateOn[el.dataset.gate as 'roles' | 'advanced'];
  }
}

function render(): void {
  app.textContent = '';
  inputs.clear();

  // Language selector on top, like the VS window.
  const langRow = document.createElement('div');
  langRow.id = 'langrow';
  const langLabel = document.createElement('label');
  langLabel.textContent = res('LabelLanguage');
  const langIcon = infoIcon('HintLanguage');
  const langSelect = document.createElement('select');
  for (const lang of LANGUAGES) {
    const opt = document.createElement('option');
    opt.value = lang.value;
    opt.textContent = lang.text;
    opt.selected = String(config['language'] ?? '') === lang.value;
    langSelect.appendChild(opt);
  }
  inputs.set('language', langSelect);
  langRow.append(langLabel);
  if (langIcon) {
    langRow.append(langIcon);
  }
  langRow.append(langSelect);
  app.appendChild(langRow);

  const tabbar = document.createElement('div');
  tabbar.id = 'tabbar';
  const pages: HTMLElement[] = [];
  SCHEMA.tabs.forEach((tab, i) => {
    const btn = document.createElement('button');
    btn.className = 'tab' + (i === 0 ? ' active' : '');
    btn.textContent = tab.title === '__tabTools' ? t('Tools') : res(tab.title);
    btn.addEventListener('click', () => {
      tabbar.querySelectorAll('.tab').forEach((b, j) => b.classList.toggle('active', j === i));
      pages.forEach((pg, j) => { pg.hidden = j !== i; });
    });
    tabbar.appendChild(btn);
  });
  app.appendChild(tabbar);

  // ⚠ NOT a <datalist>. Chromium filters its options against what the field ALREADY contains:
  // a field holding a model id offered nothing but ITSELF, and no gesture showed the others —
  // while the Visual Studio window, a combo box, lists them all whatever is in the box.
  // Measured 2026-09-03: `models/list` did return the backend's 8 models, the extension received
  // them (no failure in the log), the browser displayed one. The defect was entirely in the
  // rendering, and it did not look like one: a one-entry list reads as a backend serving one
  // model.
  // Rendering starts from scratch (app.textContent = ''), so the old popup and its target are
  // detached: forgetting them here avoids writing into a field no longer in the page.
  modelPopupTarget = null;
  modelPopup = document.createElement('div');
  modelPopup.id = 'modelpop';
  modelPopup.hidden = true;
  app.appendChild(modelPopup);

  // Initial reveal state, like VS: roles shown when any role field is set.
  gateOn.roles = ['agentModel', 'codeActionsModel', 'inlineCompletionModel', 'inlineEditModel', 'utilityModel']
    .some((key) => String(config[key] ?? '').length > 0) || config['modelRouterAuto'] === true;
  gateOn.advanced = false;

  SCHEMA.tabs.forEach((tab, i) => {
    const page = document.createElement('div');
    page.className = 'page';
    page.hidden = i !== 0;
    for (const section of tab.sections) {
      if (section.title) {
        const header = document.createElement('div');
        header.className = 'section';
        header.textContent = section.title === '__tabTools' ? t('Tools') : res(section.title);
        page.appendChild(header);
      }
      if (section.toggleGate) {
        page.appendChild(renderToggle({ gate: section.toggleGate as 'roles' | 'advanced', label: section.toggleLabel ?? '', hint: section.toggleHint ?? undefined }));
      }
      for (const field of section.fields) {
        page.appendChild(renderField(field));
      }
    }
    pages.push(page);
    app.appendChild(page);
  });

  const footer = document.createElement('div');
  footer.id = 'footer';
  const save = document.createElement('button');
  save.id = 'save';
  save.textContent = t('Save');
  save.addEventListener('click', onSave);
  statusEl = document.createElement('span');
  statusEl.id = 'status';
  footer.append(save, statusEl);
  app.appendChild(footer);

  applyGates();
}

function renderToggle(toggle: { gate: 'roles' | 'advanced'; label: string; hint?: string }): HTMLElement {
  const row = document.createElement('div');
  row.className = 'field inline';
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.id = 'g-' + toggle.gate;
  box.checked = gateOn[toggle.gate];
  box.addEventListener('change', () => {
    gateOn[toggle.gate] = box.checked;
    applyGates();
  });
  const lbl = document.createElement('label');
  lbl.htmlFor = box.id;
  lbl.textContent = toggle.label === 'LabelAdvancedBehavior' ? res('LabelAdvancedBehavior') : res(toggle.label);
  row.append(box, lbl);
  const icon = infoIcon(toggle.hint);
  if (icon) {
    row.append(icon);
  }
  return row;
}

/**
 * The model picker: one popup, anchored on the focused field.
 *
 * On open it shows the WHOLE list, like the Visual Studio combo box — that is the point of the
 * fix. Typing then narrows it, which is what the field keeps of its old datalist without keeping
 * its defect. The field stays free: a model the backend does not list (unreachable backend, an id
 * typed by hand) can still be typed, and empty still means "inherit the chat model".
 */
let modelPopup: HTMLDivElement | null = null;
let modelPopupTarget: HTMLInputElement | null = null;

function closeModelPopup(): void {
  if (modelPopup) {
    modelPopup.hidden = true;
  }
  modelPopupTarget = null;
}

function openModelPopup(box: HTMLInputElement): void {
  if (!modelPopup) {
    return;
  }
  modelPopupTarget = box;
  renderModelPopup('');
  modelPopup.hidden = false;
  const r = box.getBoundingClientRect();
  modelPopup.style.left = `${r.left + window.scrollX}px`;
  modelPopup.style.top = `${r.bottom + window.scrollY + 2}px`;
  modelPopup.style.width = `${r.width}px`;
}

function renderModelPopup(filter: string): void {
  if (!modelPopup) {
    return;
  }
  modelPopup.textContent = '';
  const needle = filter.trim().toLowerCase();
  const shown = needle ? models.filter((m) => m.toLowerCase().includes(needle)) : models;

  if (shown.length === 0) {
    // The two reasons for having nothing to show are not repaired in the same place: no model
    // at all (unreachable backend, or a refused token) is not "what you typed matches nothing".
    // An empty, silent popup would conflate them.
    const empty = document.createElement('div');
    empty.className = 'modelrow empty';
    empty.textContent = models.length === 0
      ? t('No model listed — is the backend reachable?')
      : t('No match');
    modelPopup.appendChild(empty);
    return;
  }

  for (const name of shown) {
    const row = document.createElement('div');
    row.className = 'modelrow';
    row.textContent = name;
    // mousedown, not click: it fires BEFORE the field's blur, and its preventDefault keeps the
    // field focused — otherwise the popup closes under the cursor before the pick lands.
    row.addEventListener('mousedown', (e) => {
      e.preventDefault();
      if (modelPopupTarget) {
        modelPopupTarget.value = name;
      }
      closeModelPopup();
    });
    modelPopup.appendChild(row);
  }
}

/** The caret that opens the whole list — the one gesture that was missing. */
function buildModelCaret(box: HTMLInputElement): HTMLButtonElement {
  const caret = document.createElement('button');
  caret.type = 'button';
  caret.className = 'sidebtn caret';
  caret.textContent = '▾';
  caret.title = t('Show all models');
  caret.addEventListener('mousedown', (e) => {
    e.preventDefault();
    if (modelPopupTarget === box) {
      closeModelPopup();
    } else {
      box.focus();
      openModelPopup(box);
    }
  });
  return caret;
}

function renderField(field: Field): HTMLElement {
  const row = document.createElement('div');
  const compact = field.kind === 'int' || field.kind === 'float';
  row.className = 'field' + (field.kind === 'bool' ? ' inline' : compact ? ' compact' : '');
  if (field.gate) {
    row.dataset.gate = field.gate;
  }

  const lbl = document.createElement('label');
  lbl.textContent = label(field);
  lbl.htmlFor = 'f-' + field.key;
  const icon = infoIcon(field.hint);

  let input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
  const value = config[field.key];
  switch (field.kind) {
    case 'select': {
      const select = document.createElement('select');
      for (const opt of field.options ?? []) {
        const o = document.createElement('option');
        o.value = opt.value;
        o.textContent = opt.text;
        o.selected = String(value ?? '') === opt.value;
        select.appendChild(o);
      }
      input = select;
      break;
    }
    case 'textarea': {
      const area = document.createElement('textarea');
      area.rows = 5;
      area.value = String(value ?? '');
      area.placeholder = field.hint && R[field.hint] ? R[field.hint] : '';
      input = area;
      break;
    }
    case 'bool': {
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = value === true;
      input = box;
      break;
    }
    default: {
      const box = document.createElement('input');
      box.type = field.kind === 'password' ? 'password' : 'text';
      if (field.kind === 'model') {
        box.autocomplete = 'off';
        box.addEventListener('input', () => {
          if (modelPopupTarget === box) {
            renderModelPopup(box.value);
          }
        });
        box.addEventListener('keydown', (e) => {
          if (e.key === 'ArrowDown' && modelPopupTarget !== box) {
            e.preventDefault();
            openModelPopup(box);
          } else if (e.key === 'Escape' && modelPopupTarget === box) {
            e.preventDefault();
            closeModelPopup();
          }
        });
        box.addEventListener('blur', () => {
          if (modelPopupTarget === box) {
            closeModelPopup();
          }
        });
      }
      if (compact) {
        box.classList.add('num');
      }
      box.value = String(value ?? '');
      input = box;
      break;
    }
  }
  input.id = 'f-' + field.key;
  inputs.set(field.key, input);

  if (field.kind === 'bool') {
    row.append(input, lbl);
    if (icon) {
      row.append(icon);
    }
    return row;
  }

  const labelLine = document.createElement('div');
  labelLine.className = 'labelline';
  labelLine.append(lbl);
  if (icon) {
    labelLine.append(icon);
  }

  if (compact) {
    // VS layout: label left, small right-aligned numeric field + unit on the same line.
    row.append(labelLine, input);
    if (field.unit) {
      const unit = document.createElement('span');
      unit.className = 'unit';
      unit.textContent = field.unit;
      row.append(unit);
    }
    return row;
  }

  row.append(labelLine);
  // The caret ALWAYS goes with a model field: only the chat model carries the refresh button, so
  // the five other fields had no way at all to open the list.
  const caret = field.kind === 'model' ? buildModelCaret(input as HTMLInputElement) : null;
  if (field.button || caret) {
    const line = document.createElement('div');
    line.className = 'inputline';
    line.append(input);
    if (caret) {
      line.append(caret);
    }
    if (!field.button) {
      row.append(line);
      return row;
    }
    const btn = document.createElement('button');
    btn.className = 'sidebtn';
    if (field.button === 'test') {
      btn.textContent = res('BtnTest');
      btn.addEventListener('click', () => {
        setTestStatus('…');
        vscode.postMessage({ type: 'testConnection', baseUrl: (inputs.get('baseUrl') as HTMLInputElement).value });
      });
      line.append(btn);
      testStatusEl = document.createElement('span');
      testStatusEl.className = 'teststatus';
      line.append(testStatusEl);
    } else {
      btn.textContent = '↻';
      btn.title = t('Refresh models');
      btn.addEventListener('click', () => vscode.postMessage({ type: 'refreshModels' }));
      line.append(btn);
    }
    row.append(line);
  } else {
    row.append(input);
  }
  return row;
}

function setTestStatus(text: string, ok?: boolean): void {
  if (testStatusEl) {
    testStatusEl.textContent = text;
    testStatusEl.className = 'teststatus' + (ok === undefined ? '' : ok ? ' ok' : ' ko');
  }
}

function onSave(): void {
  // Mutate the parsed original so fields this form doesn't know about survive the
  // full-JSON round trip (config/update resets absent fields to their defaults).
  const allFields: Field[] = SCHEMA.tabs.flatMap((tab) => tab.sections.flatMap((s) => s.fields))
    .concat(SCHEMA.headerFields);
  const ignored: string[] = [];
  for (const field of allFields) {
    const input = inputs.get(field.key);
    if (!input) {
      continue;
    }
    switch (field.kind) {
      case 'bool':
        config[field.key] = (input as HTMLInputElement).checked;
        break;
      // ⚠ STRICT reading, and what could not be read is NAMED (mirroring the Visual Studio
      // window). `parseInt('12abc')` is 12: the permissive reading therefore stored a value the
      // user never typed, truncated without a word. An EMPTY box is not named — it keeps the
      // current value, as before.
      case 'int': {
        const raw = input.value.trim();
        const ok = /^[+-]?\d+$/.test(raw);
        if (ok) {
          config[field.key] = parseInt(raw, 10);
        } else if (raw !== '') {
          ignored.push(labelForSentence(res(field.label)));
        }
        break;
      }
      case 'float': {
        const raw = input.value.trim().replace(',', '.');
        const ok = /^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(raw);
        if (ok) {
          config[field.key] = parseFloat(raw);
        } else if (raw !== '') {
          ignored.push(labelForSentence(res(field.label)));
        }
        break;
      }
      default:
        config[field.key] = input.value;
        break;
    }
  }
  lastIgnored = ignored;
  vscode.postMessage({ type: 'save', json: JSON.stringify(config, null, 2) });
}

/** What the status line says after a successful save: what is saved is saved, and what could not
 *  be read is named. The sentence comes from the host (the same .resx as the Visual Studio
 *  window), so both panels say the same thing in all ten languages. */
function savedStatus(): string {
  const saved = t('Settings saved.');
  return lastIgnored.length === 0
    ? saved
    : `${saved} ${fill(res('SettingsFieldsIgnored'), lastIgnored.length, lastIgnored.join(', '))}`;
}

function setStatus(text: string): void {
  if (statusEl) {
    statusEl.textContent = text;
  } else if (text) {
    // Before the form exists (e.g. host not running at 'ready'): show the message
    // standalone instead of leaving the page stuck on "Loading settings…".
    app.textContent = text;
  }
}

window.addEventListener('message', (event: MessageEvent) => {
  const msg = event.data as {
    type: string; configJson?: string; models?: string[]; strings?: Record<string, string>;
    schema?: Schema; message?: string; ok?: boolean;
  };
  switch (msg.type) {
    case 'init':
      try {
        config = JSON.parse(msg.configJson ?? '{}') as Record<string, unknown>;
      } catch {
        config = {};
      }
      models = msg.models ?? [];
      R = msg.strings ?? {};
      // The form comes from the host (Core schema). An empty one means the RPC failed: say so
      // rather than rendering a blank page that looks like a working, empty settings window.
      SCHEMA = msg.schema ?? { tabs: [], headerFields: [] };
      if (SCHEMA.tabs.length === 0) {
        app.textContent = t('Settings could not be loaded — the Inferpal host did not answer.');
        break;
      }
      render();
      break;
    case 'models': {
      models = msg.models ?? [];
      // An open popup must reflect the list just re-read: otherwise the ↻ button would have no
      // visible effect until the next time it is opened.
      if (modelPopupTarget) {
        renderModelPopup(modelPopupTarget.value);
      }
      break;
    }
    case 'testResult':
      setTestStatus(msg.ok ? t('Connected') : t('Backend unreachable'), msg.ok);
      break;
    case 'saveDone':
      setStatus(msg.ok ? savedStatus() : '');
      // ⚠ The message only clears itself when it has nothing to teach: "saved" reads at a glance,
      // while the list of ignored fields is what the user must be able to re-read in order to go
      // and fix their input.
      if (msg.ok && lastIgnored.length === 0) {
        setTimeout(() => setStatus(''), 2500);
      }
      break;
    case 'error':
      setStatus(msg.message ?? 'error');
      break;
  }
});

app.textContent = t('Loading settings…');
vscode.postMessage({ type: 'ready' });
