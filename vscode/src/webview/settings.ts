// Inferpal Settings webview: the pages of the Visual Studio settings window — same pages, same
// sections, same labels and hints (served localized by the host from the shared .resx via
// `settings/strings`), the same side navigation and search, compact numeric fields with unit
// suffixes, the "Show advanced settings" fold, Test / refresh buttons and a footer counting the
// unsaved changes. The config JSON is mutated in place and saved WHOLE (config/update replaces the
// config — absent fields would reset).
import { fill, t } from './l10n';
import { icon, setIcon } from './icons';
import { createEditor, type StructuredEditor } from './settingsEditors';
import { createWidget, type LiveWidget } from './settingsWidgets';

const vscode = acquireVsCodeApi();

// Localized resources pushed by the extension (resx resource name → translated string).
let R: Record<string, string> = {};
const res = (key: string | null | undefined): string => (key ? R[key] ?? key : '');

// The fields the last save could not read: decided when sending, rendered when the host answers
// (`saveDone`), which is the only moment we know the save actually happened.
let lastIgnored: string[] = [];
/** Permission rules the host could not read at the last save. */
let lastRulesIgnored = 0;

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
// The schema shapes come from protocol.ts, never redeclared here -
// a second set of interfaces for the same JSON - and that is what left this panel ignoring
// `defaultValue`: the property existed host-side and was missing from the local copy. Types are
// erased at build time, so the import costs the bundle nothing.
import type {
  SettingsField as Field, SettingsSchema as Schema, SettingsSection as Section, SettingsTab as Tab,
} from '../protocol';

/** Served by the host at init; empty until then. */
let SCHEMA: Schema = { tabs: [] };

const app = document.getElementById('app')!;
let config: Record<string, unknown> = {};
/** The configuration as last SAVED, while a Save is on its way: `config` already holds the edits it sends, and the
 *  unsaved count compares the form against `config`. A Save the host refuses puts this back. */
let savedBeforeSave: Record<string, unknown> | null = null;
let models: string[] = [];
const inputs = new Map<string, HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>();
const fieldsByKey = new Map<string, Field>();
let statusEl: HTMLElement | null = null;
let unsavedEl: HTMLElement | null = null;
let saveBtn: HTMLButtonElement | null = null;
let testStatusEl: HTMLElement | null = null;
let currentPage = '';
/** Per page: whether its "Show advanced settings" box is checked. */
const foldOpen = new Map<string, boolean>();
const navButtons = new Map<string, HTMLButtonElement>();
const pageEls = new Map<string, HTMLElement>();
let noMatchEl: HTMLElement | null = null;
/** Elements whose text depends on the form: re-rendered on every edit. */
const liveWidgets: (() => void)[] = [];
/** The structured editors drawn over the list settings, which receive the host's answers. */
const editors: StructuredEditor[] = [];
/** The live blocks of the pages (index, @Docs, usage…), with the page each one is on. */
const widgets: { page: string; widget: LiveWidget }[] = [];

// ── Rendering ────────────────────────────────────────────────────────────────

function render(): void {
  app.textContent = '';
  inputs.clear();
  fieldsByKey.clear();
  navButtons.clear();
  pageEls.clear();
  liveWidgets.length = 0;
  editors.length = 0;
  widgets.length = 0;
  for (const tab of SCHEMA.tabs) {
    for (const field of tab.sections.flatMap((s) => s.fields)) {
      fieldsByKey.set(field.key, field);
    }
  }
  if (!currentPage || !SCHEMA.tabs.some((tab) => tab.key === currentPage)) {
    currentPage = SCHEMA.tabs[0]?.key ?? '';
  }

  const layout = document.createElement('div');
  layout.id = 'layout';
  layout.append(renderNav(), renderMain());
  app.appendChild(layout);
  app.appendChild(renderFooter());

  // ⚠ NOT a <datalist>. Chromium filters its options against what the field ALREADY contains:
  // a field holding a model id offered nothing but ITSELF, and no gesture showed the others —
  // while the Visual Studio window, a combo box, lists them all whatever is in the box.
  // `models/list` can return the backend's models, the extension receive
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

  showPage(currentPage, true);
  refreshForm();
}

/** A select drawn as cards — one radio per option, its name and its sentence. The select stays the value Save reads. */
function cardsField(field: Field, select: HTMLSelectElement): HTMLElement {
  const wrap = document.createElement('div');
  wrap.className = 'field wide';
  wrap.id = 'row-' + field.key;
  if (field.key === 'inlineCompletionMode') {
    wrap.appendChild(ghostPreview());
  }
  const set = document.createElement('fieldset');
  set.className = 'cards';
  const legend = document.createElement('legend');
  legend.textContent = res(field.label);
  const grid = document.createElement('div');
  grid.className = 'cardgrid';
  for (const opt of field.options ?? []) {
    const card = document.createElement('label');
    card.className = 'choicecard';
    const radio = document.createElement('input');
    radio.type = 'radio';
    radio.name = 'cards-' + field.key;
    radio.value = opt.value;
    radio.checked = select.value === opt.value;
    radio.addEventListener('change', () => {
      select.value = opt.value;
      refreshForm();
    });
    const name = document.createElement('span');
    name.className = 'choicename';
    name.append(radio, document.createTextNode(opt.text));
    card.appendChild(name);
    if (opt.description) {
      const d = document.createElement('span');
      d.className = 'hint';
      d.textContent = opt.description;
      card.appendChild(d);
    }
    grid.appendChild(card);
  }
  select.hidden = true;
  select.id = 'f-' + field.key;
  set.append(legend, grid, select);
  wrap.appendChild(set);
  return wrap;
}

/** A select drawn as a segmented switch — one radio per option; the select stays the value Save reads. */
function segmentedField(field: Field, select: HTMLSelectElement, hint: HTMLElement | null): HTMLElement {
  const wrap = document.createElement('div');
  wrap.className = 'field';
  wrap.id = 'row-' + field.key;
  const set = document.createElement('fieldset');
  set.className = 'segmented';
  const legend = document.createElement('legend');
  legend.textContent = res(field.label);
  const group = document.createElement('div');
  group.className = 'segments';
  for (const opt of field.options ?? []) {
    const seg = document.createElement('label');
    seg.className = 'segment';
    const radio = document.createElement('input');
    radio.type = 'radio';
    radio.name = 'seg-' + field.key;
    radio.value = opt.value;
    radio.checked = select.value === opt.value;
    radio.addEventListener('change', () => {
      select.value = opt.value;
      refreshForm();
    });
    seg.append(radio, document.createTextNode(opt.text));
    group.appendChild(seg);
  }
  select.hidden = true;
  select.id = 'f-' + field.key;
  set.append(legend, group, select);
  wrap.appendChild(set);
  if (hint) {
    wrap.appendChild(hint);
  }
  return wrap;
}

/** What a suggestion looks like: the code typed, and the grey rest Tab would accept. Decorative. */
function ghostPreview(): HTMLElement {
  const pre = document.createElement('pre');
  pre.className = 'ghostpreview';
  pre.setAttribute('aria-hidden', 'true');
  const typed = (text: string): Text => document.createTextNode(text);
  const ghost = document.createElement('span');
  ghost.className = 'ghost';
  ghost.textContent = 'Sum(l => l.Price * l.Quantity);';
  pre.append(typed('public decimal Total(IEnumerable<Line> lines)\n{\n    return lines.'), ghost, typed('\n}'));
  return pre;
}

function renderNav(): HTMLElement {
  const nav = document.createElement('nav');
  nav.id = 'nav';
  nav.setAttribute('aria-label', res('SettingsSearchPlaceholder'));

  const search = document.createElement('div');
  search.className = 'search';
  search.appendChild(icon('search', 14));
  const box = document.createElement('input');
  box.type = 'search';
  box.placeholder = res('SettingsSearchPlaceholder');
  box.setAttribute('aria-label', res('SettingsSearchPlaceholder'));
  box.addEventListener('keyup', () => applySearch(box.value));
  box.addEventListener('search', () => applySearch(box.value));
  search.appendChild(box);
  nav.appendChild(search);

  for (const tab of SCHEMA.tabs) {
    const btn = document.createElement('button');
    btn.className = 'navitem';
    btn.textContent = res(tab.title);
    btn.addEventListener('click', () => showPage(tab.key));
    navButtons.set(tab.key, btn);
    nav.appendChild(btn);
  }
  noMatchEl = document.createElement('p');
  noMatchEl.className = 'nomatch';
  noMatchEl.textContent = res('SettingsSearchNoMatch');
  noMatchEl.hidden = true;
  nav.appendChild(noMatchEl);
  return nav;
}

function renderMain(): HTMLElement {
  const main = document.createElement('main');
  main.id = 'main';
  for (const tab of SCHEMA.tabs) {
    main.appendChild(renderPage(tab));
  }
  return main;
}

function renderPage(tab: Tab): HTMLElement {
  const page = document.createElement('section');
  page.className = 'page';
  page.dataset.page = tab.key;
  pageEls.set(tab.key, page);

  const head = document.createElement('header');
  const h1 = document.createElement('h1');
  h1.textContent = res(tab.title);
  const desc = document.createElement('p');
  desc.className = 'pagedesc';
  desc.textContent = res(tab.description);
  head.append(h1, desc);
  page.appendChild(head);

  const advanced = tab.advancedToggle
    ? tab.sections.filter((s) => s.gate === 'advanced')
    : [];
  if (!foldOpen.has(tab.key)) {
    // A setting in effect is never hidden behind the fold: the page opens with it shown when a field
    // the schema marks `opensFold` departs from its factory value — the rule of ModelRoleSettings.
    foldOpen.set(tab.key, advanced.some((s) => s.fields.some((f) => f.opensFold && departsFromDefault(f))));
  }

  for (const section of tab.sections.filter((s) => s.gate !== 'advanced')) {
    page.appendChild(renderSection(section, tab.key));
  }
  if (advanced.length > 0) {
    const toggle = document.createElement('div');
    toggle.className = 'foldtoggle';
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.id = 'fold-' + tab.key;
    box.checked = foldOpen.get(tab.key) === true;
    const lbl = document.createElement('label');
    lbl.htmlFor = box.id;
    lbl.textContent = res(tab.advancedToggle);
    toggle.append(box, lbl);
    page.appendChild(toggle);
    const fold = document.createElement('div');
    fold.className = 'fold';
    fold.hidden = !box.checked;
    // The box folds; it never writes a value.
    box.addEventListener('change', () => {
      foldOpen.set(tab.key, box.checked);
      fold.hidden = !box.checked;
    });
    for (const section of advanced) {
      fold.appendChild(renderSection(section, tab.key));
    }
    page.appendChild(fold);
  }
  return page;
}

function departsFromDefault(field: Field): boolean {
  const value = config[field.key];
  const factory = field.defaultValue ?? '';
  return field.kind === 'bool'
    ? String(value === true) !== factory
    : String(value ?? '').trim() !== factory.trim();
}

function renderSection(section: Section, pageKey: string): HTMLElement {
  const wrap = document.createElement(section.collapsible ? 'details' : 'section');
  wrap.className = 'block';
  if (section.title) {
    const heading = document.createElement(section.collapsible ? 'summary' : 'h2');
    heading.textContent = res(section.title);
    wrap.appendChild(heading);
  }
  if (section.description) {
    const d = document.createElement('p');
    d.className = 'sectiondesc';
    d.textContent = res(section.description);
    wrap.appendChild(d);
  }
  const body = document.createElement('div');
  body.className = section.grid ? 'fields grid' : 'fields';
  for (const field of section.fields) {
    body.appendChild(renderField(field));
  }
  if (section.fields.length > 0) {
    wrap.appendChild(body);
  }
  // A live block goes under the fields and above the section's note, which qualifies it.
  if (section.widget) {
    const widget = createWidget(section.widget, pageKey, widgetHost);
    if (widget) {
      widgets.push({ page: pageKey, widget });
      wrap.appendChild(widget.el);
      if (widget.onFormChanged) {
        liveWidgets.push(widget.onFormChanged);
      }
    }
  }
  if (section.note) {
    const note = document.createElement('p');
    note.className = 'note';
    note.textContent = res(section.note);
    wrap.appendChild(note);
  }
  return wrap;
}

/** What the live blocks need from the form: its words, its values, and the way to another page. */
const widgetHost = {
  res,
  post: (msg: Record<string, unknown>) => vscode.postMessage(msg),
  formValue: (key: string): string => {
    const input = inputs.get(key);
    return input ? input.value : String(config[key] ?? '');
  },
  setFormValue: (key: string, value: string): void => {
    // As a pick from a model field's list does: the value, then the recount of unsaved changes.
    const input = inputs.get(key);
    if (input) {
      input.value = value;
      refreshForm();
    }
  },
  showPage: (page: string, fieldKey?: string): void => {
    // The page that holds the field, when it is not the one named: the field decides.
    const holder = fieldKey
      ? SCHEMA.tabs.find((tab) => tab.sections.some((s) => s.fields.some((f) => f.key === fieldKey)))?.key
      : undefined;
    const target = holder ?? page;
    if (fieldKey && SCHEMA.tabs.find((t) => t.key === target)?.sections
      .some((s) => s.gate === 'advanced' && s.fields.some((f) => f.key === fieldKey))) {
      // A field behind the fold opens it: a link to a box nobody can see is a link to nothing.
      const box = document.getElementById('fold-' + target) as HTMLInputElement | null;
      if (box && !box.checked) {
        box.click();
      }
    }
    showPage(target);
    const row = fieldKey ? document.getElementById('row-' + fieldKey) ?? inputs.get(fieldKey)?.closest('.field') : null;
    row?.scrollIntoView({ block: 'start' });
    (fieldKey ? inputs.get(fieldKey) : undefined)?.focus();
  },
  isShown: (page: string): boolean => currentPage === page,
};

function renderFooter(): HTMLElement {
  const footer = document.createElement('footer');
  footer.id = 'footer';
  unsavedEl = document.createElement('span');
  unsavedEl.id = 'unsaved';
  statusEl = document.createElement('span');
  statusEl.id = 'status';
  const cancel = document.createElement('button');
  cancel.className = 'secondary';
  cancel.textContent = res('SettingsCancel');
  // Cancel puts every box back to what is saved; nothing is written.
  cancel.addEventListener('click', () => {
    setStatus('');
    render();
  });
  saveBtn = document.createElement('button');
  saveBtn.id = 'save';
  saveBtn.textContent = res('BtnSave');
  saveBtn.addEventListener('click', onSave);
  footer.append(unsavedEl, statusEl, cancel, saveBtn);
  return footer;
}

function showPage(key: string, rendered = false): void {
  const changed = currentPage !== key;
  currentPage = key;
  for (const [k, page] of pageEls) {
    page.hidden = k !== key;
  }
  // The live blocks of the page ask again: the index or the conversation may have moved since.
  if (changed || rendered) {
    for (const w of widgets) {
      if (w.page === key) {
        w.widget.onShown?.();
      }
    }
  }
  for (const [k, btn] of navButtons) {
    if (k === key) {
      btn.setAttribute('aria-current', 'page');
    } else {
      btn.removeAttribute('aria-current');
    }
  }
}

/** Narrows the navigation to the pages that mention the query, and opens the first of them. */
function applySearch(query: string): void {
  const q = query.trim().toLowerCase();
  let first: string | null = null;
  for (const [key, btn] of navButtons) {
    const page = pageEls.get(key);
    const match = q.length === 0 || (page?.textContent ?? '').toLowerCase().includes(q);
    btn.hidden = !match;
    if (match && first === null) {
      first = key;
    }
  }
  if (noMatchEl) {
    noMatchEl.hidden = first !== null;
  }
  if (first !== null && navButtons.get(currentPage)?.hidden) {
    showPage(first);
  }
}

// ── Fields ───────────────────────────────────────────────────────────────────

/** The hint of a field: the non-Ollama one when the form's server is not Ollama. */
function hintOf(field: Field): string {
  if (field.hintNotOllama) {
    const provider = (inputs.get('provider') as HTMLSelectElement | undefined)?.value ?? String(config['provider'] ?? 'ollama');
    if (provider !== 'ollama') {
      return res(field.hintNotOllama);
    }
  }
  return res(field.hint);
}

function hintElement(field: Field): HTMLElement | null {
  if (!field.hint) {
    return null;
  }
  const hint = document.createElement('span');
  hint.className = 'hint';
  hint.id = 'h-' + field.key;
  hint.textContent = hintOf(field);
  if (field.hintNotOllama) {
    liveWidgets.push(() => { hint.textContent = hintOf(field); });
  }
  return hint;
}

/**
 * The model picker: one popup, anchored on the focused field.
 *
 * On open it shows the WHOLE list, like the Visual Studio combo box — that is the point of the
 * fix. The field is read-only, like Visual Studio's non-editable combo boxes: a model is picked,
 * never typed (a typo saved a model the backend does not serve). The optional models open the list
 * on an empty entry — "the chat model" for a task, "automatic" for code search — the leading "" of
 * the Visual Studio window.
 */
let modelPopup: HTMLDivElement | null = null;
let modelPopupTarget: HTMLInputElement | null = null;
// The rows of the open popup and the one the keyboard points at: on a read-only field, the arrow
// keys and Enter are the only way to pick without a mouse.
let modelChoices: string[] = [];
let modelHighlight = -1;

function closeModelPopup(): void {
  if (modelPopup) {
    modelPopup.hidden = true;
  }
  modelPopupTarget = null;
  modelHighlight = -1;
}

function openModelPopup(box: HTMLInputElement): void {
  if (!modelPopup) {
    return;
  }
  modelPopupTarget = box;
  modelHighlight = -1;
  renderModelPopup();
  modelPopup.hidden = false;
  const r = box.getBoundingClientRect();
  modelPopup.style.left = `${r.left + window.scrollX}px`;
  modelPopup.style.top = `${r.bottom + window.scrollY + 2}px`;
  modelPopup.style.width = `${r.width}px`;
}

function renderModelPopup(): void {
  if (!modelPopup) {
    return;
  }
  modelPopup.textContent = '';
  // An optional model is cleared by picking the empty entry: on a read-only field, that is the only
  // gesture that can.
  modelChoices = modelPopupTarget?.dataset.optional === 'true' ? ['', ...models] : models;
  if (modelHighlight < 0 || modelHighlight >= modelChoices.length) {
    // The starting row is the current value: Enter without an arrow key keeps it.
    modelHighlight = modelChoices.indexOf(modelPopupTarget?.value ?? '');
  }

  if (models.length === 0) {
    // No model at all (unreachable backend, or a refused token): an empty, silent popup would read
    // as a backend serving nothing. An optional model's empty entry is still offered.
    const empty = document.createElement('div');
    empty.className = 'modelrow empty';
    empty.textContent = t('No model listed — is the backend reachable?');
    modelPopup.appendChild(empty);
  }

  modelChoices.forEach((name, index) => {
    const row = document.createElement('div');
    row.className = 'modelrow' + (index === modelHighlight ? ' active' : '');
    row.textContent = name === '' ? modelPopupTarget?.dataset.empty ?? '—' : name;
    // mousedown, not click: it fires BEFORE the field's blur, and its preventDefault keeps the
    // field focused — otherwise the popup closes under the cursor before the pick lands.
    row.addEventListener('mousedown', (e) => {
      e.preventDefault();
      pickModel(name);
    });
    modelPopup?.appendChild(row);
  });
}

function pickModel(name: string | undefined): void {
  if (modelPopupTarget && name !== undefined) {
    modelPopupTarget.value = name;
    refreshForm();
  }
  closeModelPopup();
}

function moveModelHighlight(delta: number): void {
  if (!modelPopup || modelChoices.length === 0) {
    return;
  }
  const count = modelChoices.length;
  modelHighlight = modelHighlight < 0 ? (delta > 0 ? 0 : count - 1) : (modelHighlight + delta + count) % count;
  const rows = modelPopup.querySelectorAll<HTMLElement>('.modelrow:not(.empty)');
  rows.forEach((row, index) => row.classList.toggle('active', index === modelHighlight));
  rows[modelHighlight]?.scrollIntoView({ block: 'nearest' });
}

/**
 * Fills a drop-down list and selects the option of the saved value (case ignored). A value no option
 * names stays in place, as an entry of its own: with no option selected the browser shows the first
 * one, and Save wrote it instead of the value in effect.
 */
function fillSelect(
  select: HTMLSelectElement,
  options: ReadonlyArray<{ value: string; text: string }>,
  value: unknown,
): void {
  const current = String(value ?? '');
  const match = options.find((o) => o.value === current)
    ?? options.find((o) => o.value.toLowerCase() === current.trim().toLowerCase());
  for (const opt of options) {
    const o = document.createElement('option');
    o.value = opt.value;
    o.textContent = opt.text;
    o.selected = opt === match;
    select.appendChild(o);
  }
  if (!match) {
    const o = document.createElement('option');
    o.value = current;
    o.textContent = current === '' ? '—' : current;
    o.selected = true;
    select.appendChild(o);
  }
}

/** The caret that opens the whole list — the one gesture that was missing. */
function buildModelCaret(box: HTMLInputElement): HTMLButtonElement {
  const caret = document.createElement('button');
  caret.type = 'button';
  caret.className = 'sidebtn caret';
  setIcon(caret, 'chevronDown', undefined, 13);
  caret.title = t('Show all models');
  caret.setAttribute('aria-label', t('Show all models'));
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
  row.className = 'field' + (field.kind === 'bool' ? ' check' : field.kind === 'textarea' ? ' wide' : '');

  const lbl = document.createElement('label');
  lbl.textContent = res(field.label);
  lbl.htmlFor = 'f-' + field.key;
  const hint = hintElement(field);

  let input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
  const value = config[field.key];
  switch (field.kind) {
    case 'select': {
      const select = document.createElement('select');
      // "Automatic" is the one option whose text is localized: the language names never are.
      const options = (field.options ?? []).map((o) =>
        field.key === 'language' && o.value === '' ? { value: '', text: res('LangAuto') } : o);
      fillSelect(select, options, value);
      select.addEventListener('change', refreshForm);
      input = select;
      if (field.editor === 'cards') {
        // Drawn as cards, each with its sentence; the select stays the value Save reads and the unsaved count compares.
        inputs.set(field.key, select);
        return cardsField(field, select);
      }
      if (field.editor === 'segmented') {
        inputs.set(field.key, select);
        return segmentedField(field, select, hint);
      }
      break;
    }
    case 'textarea': {
      const area = document.createElement('textarea');
      area.rows = 4;
      area.value = String(value ?? '');
      area.spellcheck = false;
      input = area;
      break;
    }
    case 'bool': {
      const box = document.createElement('input');
      box.type = 'checkbox';
      // An inverted box shows the opposite of the stored value: the key names what is turned OFF.
      box.checked = field.inverted ? value !== true : value === true;
      box.addEventListener('change', refreshForm);
      input = box;
      break;
    }
    default: {
      const box = document.createElement('input');
      box.type = field.kind === 'password' ? 'password' : 'text';
      if (field.kind === 'model') {
        box.autocomplete = 'off';
        // Read-only, like Visual Studio's non-editable combo boxes: a model is picked, never typed.
        box.readOnly = true;
        box.style.cursor = 'pointer';
        // Every model but the chat model can be left empty, and the empty value says what does the work then.
        if (field.key !== 'defaultModel') {
          box.dataset.optional = 'true';
          box.dataset.empty = res(field.emptyChoice) || '—';
          box.placeholder = box.dataset.empty;
        }
        box.addEventListener('mousedown', (e) => {
          e.preventDefault();
          box.focus();
          if (modelPopupTarget === box) {
            closeModelPopup();
          } else {
            openModelPopup(box);
          }
        });
        box.addEventListener('keydown', (e) => {
          if (modelPopupTarget !== box) {
            if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') {
              e.preventDefault();
              openModelPopup(box);
            }
            return;
          }
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            moveModelHighlight(1);
          } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            moveModelHighlight(-1);
          } else if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            pickModel(modelChoices[modelHighlight]);
          } else if (e.key === 'Escape') {
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
      box.value = shownValue(field, value);
      input = box;
      break;
    }
  }
  input.id = 'f-' + field.key;
  if (hint) {
    input.setAttribute('aria-describedby', hint.id);
  }
  inputs.set(field.key, input);

  if (field.kind === 'bool') {
    const text = document.createElement('div');
    text.className = 'checktext';
    text.append(lbl);
    if (hint) {
      text.append(hint);
    }
    row.append(input, text);
    return row;
  }

  row.append(lbl);
  row.id = 'row-' + field.key;
  if (field.kind === 'textarea' && field.editor) {
    const editor = createEditor(field, input as HTMLTextAreaElement, editorHost);
    if (editor) {
      editors.push(editor);
      if (hint) {
        row.append(hint);
      }
      // The label names the TEXT ("Rules, one per line"), or repeats the section title ("MCP servers"): the list
      // view goes without it. Pinned files keep theirs, the name of the list under "Always in the prompt".
      const textOnlyLabel = field.editor !== 'pinnedFiles' ? lbl : null;
      if (textOnlyLabel) {
        textOnlyLabel.hidden = true;
      }
      row.append(withTextView(field, editor, input as HTMLTextAreaElement, textOnlyLabel));
      return row;
    }
  }
  if (compact) {
    // A small right-aligned box with its unit on the same line.
    const line = document.createElement('div');
    line.className = 'inputline';
    line.append(input);
    if (field.unit) {
      const unit = document.createElement('span');
      unit.className = 'unit';
      unit.textContent = res(field.unit);
      line.append(unit);
    }
    row.append(line);
    if (hint) {
      row.append(hint);
    }
    return row;
  }

  // The caret ALWAYS goes with a model field: only the chat model carries the refresh button, so
  // the other fields had no other way to open the list.
  const caret = field.kind === 'model' ? buildModelCaret(input as HTMLInputElement) : null;
  if (field.button || caret) {
    const line = document.createElement('div');
    line.className = 'inputline';
    line.append(input);
    if (caret) {
      line.append(caret);
    }
    if (field.button === 'test') {
      const btn = document.createElement('button');
      btn.className = 'sidebtn';
      btn.textContent = res('BtnTest');
      btn.addEventListener('click', () => {
        setTestStatus('…');
        // The key in the form travels with the URL: probing a new server with the SAVED key read
        // "unreachable" on a server that only refused the old (or missing) key.
        vscode.postMessage({
          type: 'testConnection',
          baseUrl: (inputs.get('baseUrl') as HTMLInputElement).value,
          apiKey:  (inputs.get('apiKey')  as HTMLInputElement | undefined)?.value,
        });
      });
      line.append(btn);
    } else if (field.button === 'refreshModels') {
      const btn = document.createElement('button');
      btn.className = 'sidebtn';
      setIcon(btn, 'retry', undefined, 13);
      btn.title = t('Refresh models');
      btn.setAttribute('aria-label', t('Refresh models'));
      // ⚠ The FORM's values, not the saved ones: without them the refresh listed the models of
      // the old URL after you typed a new one. Same class as the Test button - the panel acts on
      // what is saved while the user is looking at what they typed.
      btn.addEventListener('click', () => vscode.postMessage({
        type: 'refreshModels',
        baseUrl:  (inputs.get('baseUrl')  as HTMLInputElement | undefined)?.value,
        provider: (inputs.get('provider') as HTMLSelectElement | undefined)?.value,
        apiKey:   (inputs.get('apiKey')   as HTMLInputElement | undefined)?.value,
      }));
      line.append(btn);
    }
    row.append(line);
    if (field.button === 'test') {
      testStatusEl = document.createElement('span');
      testStatusEl.className = 'teststatus';
      row.append(testStatusEl);
    }
  } else {
    row.append(input);
  }
  if (hint) {
    row.append(hint);
  }
  return row;
}

/** What the structured editors use of the panel: the strings, the host, and the text box they write. */
const editorHost = {
  res: (key: string): string => res(key),
  post: (msg: Record<string, unknown>): void => vscode.postMessage(msg),
  commit: (area: HTMLTextAreaElement, text: string): void => {
    area.value = text;
    refreshForm();
  },
};

/**
 * The structured editor and the setting's text, one shown at a time. The text stays one click away: it is what
 * is saved, and the only editor for a line the structured one cannot read.
 */
function withTextView(field: Field, editor: StructuredEditor, area: HTMLTextAreaElement,
                      textOnlyLabel: HTMLElement | null): HTMLElement {
  const wrap = document.createElement('div');
  wrap.className = 'editorwrap';
  const bar = document.createElement('div');
  bar.className = 'editorbar';
  const toggle = document.createElement('button');
  toggle.type = 'button';
  toggle.className = 'linkbtn';
  const asText = res(field.editor === 'mcpServers' ? 'SettingsEditAsJson' : 'SettingsEditAsText');
  toggle.textContent = asText;
  area.hidden = true;
  area.rows = field.editor === 'mcpServers' ? 10 : 5;
  toggle.addEventListener('click', () => {
    const showText = area.hidden;
    area.hidden = !showText;
    editor.view.hidden = showText;
    if (textOnlyLabel) {
      textOnlyLabel.hidden = !showText;
    }
    toggle.textContent = showText ? res('SettingsEditAsList') : asText;
    if (showText) {
      area.focus();
    } else {
      editor.refresh();
    }
  });
  bar.append(toggle);
  wrap.append(bar, editor.view, area);
  return wrap;
}

function setTestStatus(text: string, ok?: boolean): void {
  if (testStatusEl) {
    testStatusEl.textContent = text;
    testStatusEl.className = 'teststatus' + (ok === undefined ? '' : ok ? ' ok' : ' ko');
  }
}

// ── Unsaved changes ──────────────────────────────────────────────────────────

/** What a box would save, as text — compared with what the configuration holds. */
function formText(field: Field): string | null {
  const input = inputs.get(field.key);
  if (!input) {
    return null;
  }
  if (field.kind === 'bool') {
    const checked = (input as HTMLInputElement).checked;
    return String(field.inverted ? !checked : checked);
  }
  return sameLineEnds(input.value).trim();
}

function savedText(field: Field): string {
  const value = config[field.key];
  return field.kind === 'bool' ? String(value === true) : sameLineEnds(shownValue(field, value)).trim();
}

/** What a box shows for a stored value: a 0 that means "not set" shows empty, as the field's hint says. */
function shownValue(field: Field, value: unknown): string {
  return field.zeroIsEmpty && Number(value) === 0 ? '' : String(value ?? '');
}

/**
 * A text box hands back its text with LF line ends whatever it was given, while a setting written on Windows (the
 * MCP server JSON the Visual Studio window saves) carries CRLF: compared as is, the panel opened with an unsaved
 * change nobody made.
 */
function sameLineEnds(text: string): string {
  return text.replace(/\r\n/g, '\n');
}

/** Counts the boxes that differ from the saved configuration, and refreshes the live summaries. */
function refreshForm(): void {
  let changed = 0;
  for (const field of fieldsByKey.values()) {
    const now = formText(field);
    if (now !== null && now !== savedText(field)) {
      changed++;
    }
  }
  if (unsavedEl) {
    unsavedEl.textContent = changed === 0 ? res('SettingsNoUnsavedChanges') : fill(res('SettingsUnsavedChanges'), changed);
  }
  if (saveBtn) {
    saveBtn.disabled = changed === 0;
  }
  for (const update of liveWidgets) {
    update();
  }
}

// ── Save ─────────────────────────────────────────────────────────────────────

/** Clearing a numeric box RESTORES THE DEFAULT - the Visual Studio window affordance
 *  (SettingsFallback: "empty" = restore the default, "unreadable" = keep what is configured).
 *  This panel did not know those defaults, so the same gesture did nothing here: the host now
 *  serves them with the schema. With no known default we touch nothing - the old behaviour beats
 *  an invented value. */
function applyDefault(
  config: Record<string, unknown>, field: Field, parse: (s: string, radix?: number) => number,
): void {
  const raw = field.defaultValue;
  if (raw === undefined || raw === null) {
    return;
  }
  const value = parse(raw, 10);
  if (!Number.isNaN(value)) {
    config[field.key] = value;
  }
}

function onSave(): void {
  // Mutate the parsed original so fields this form doesn't know about survive the
  // full-JSON round trip (config/update resets absent fields to their defaults).
  // The advanced fold never takes part: a folded section is saved like any other.
  // ⚠ On a COPY, the saved state kept aside until the host answers: written in place, a Save the host refused (a turn
  // running, no host) still counted as done — "No unsaved changes", and Cancel redrew the refused values as saved.
  savedBeforeSave = config;
  config = JSON.parse(JSON.stringify(config)) as Record<string, unknown>;
  const ignored: string[] = [];
  for (const field of fieldsByKey.values()) {
    const input = inputs.get(field.key);
    if (!input) {
      continue;
    }
    switch (field.kind) {
      case 'bool': {
        const checked = (input as HTMLInputElement).checked;
        config[field.key] = field.inverted ? !checked : checked;
        break;
      }
      // ⚠ STRICT reading, and what could not be read is NAMED (mirroring the Visual Studio
      // window). `parseInt('12abc')` is 12: the permissive reading therefore stored a value the
      // user never typed, truncated without a word. An EMPTY box restores the default.
      case 'int': {
        const raw = input.value.trim();
        const ok = /^[+-]?\d+$/.test(raw);
        const value = ok ? parseInt(raw, 10) : NaN;
        // ⚠ The bounds come from the Core schema, as the Visual Studio window reads them: a value outside them is
        // applied no more than a typo is — named, and the saved value kept. Stored as typed, 50 results per search
        // went to a search the other window caps at 20.
        if (ok && (field.min == null || value >= field.min) && (field.max == null || value <= field.max)) {
          config[field.key] = value;
        } else if (raw === '') {
          applyDefault(config, field, parseInt);
        } else {
          ignored.push(labelForSentence(res(field.label)));
        }
        break;
      }
      case 'float': {
        const raw = input.value.trim().replace(',', '.');
        const ok = /^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(raw);
        if (ok) {
          config[field.key] = parseFloat(raw);
        } else if (raw === '') {
          applyDefault(config, field, parseFloat);
        } else {
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
  const parts = [t('Settings saved.')];
  if (lastIgnored.length > 0) {
    parts.push(fill(res('SettingsFieldsIgnored'), lastIgnored.length, lastIgnored.join(', ')));
  }
  // A DIFFERENT fact from the previous one, not a variant: the rules field IS saved, it is some of
  // its LINES that are inert. Confusing the two would tell the user they lost what they typed while
  // it is right there.
  if (lastRulesIgnored > 0) {
    parts.push(fill(res('SettingsPermissionRulesIgnored'), lastRulesIgnored));
  }
  return parts.join(' ');
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
    schema?: Schema; message?: string; ok?: boolean; rulesIgnored?: number; provider?: string | null;
    refused?: string | null; op?: string;
  };
  // The live blocks and the structured editors take the answers to their own requests (cards, rules table, index…).
  // Every widget sees a message before an editor may consume it: the agent page's rules card counts the table the
  // rules editor asked for.
  let consumed = false;
  for (const w of widgets) {
    consumed = w.widget.onMessage?.(event.data as { type: string }) === true || consumed;
  }
  if (editors.some((editor) => editor.onMessage?.(event.data as { type: string }) === true) || consumed) {
    return;
  }
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
      SCHEMA = msg.schema ?? { tabs: [] };
      if (SCHEMA.tabs.length === 0) {
        app.textContent = t('Settings could not be loaded — the Inferpal host did not answer.');
        break;
      }
      foldOpen.clear();
      render();
      break;
    case 'models': {
      models = msg.models ?? [];
      // An open popup must reflect the list just re-read: otherwise the refresh button would have
      // no visible effect until the next time it is opened.
      if (modelPopupTarget) {
        renderModelPopup();
      }
      break;
    }
    case 'testResult': {
      // The detected backend is PRE-SELECTED, as in the Visual Studio window: without that, a URL
      // answering as another provider than the one in the dropdown leaves the user guessing which
      // to pick - and the probe has just named it.
      const detected = msg.provider ?? null;
      let named: string | null = null;
      if (detected) {
        const select = inputs.get('provider') as HTMLSelectElement | undefined;
        if (select) {
          const option = [...select.options].find((o) => o.value === detected);
          if (option) {
            select.value = detected;
            named = option.textContent;
            refreshForm();
          }
        }
      }
      // A server that refused the probe (a wrong API key) is up: the very thing this button checks.
      setTestStatus(
        msg.ok ? (named ? `${t('Connected')} — ${named}` : t('Connected')) : (msg.refused ?? t('Backend unreachable')),
        msg.ok);
      break;
    }
    case 'saveDone':
      savedBeforeSave = null;
      lastRulesIgnored = msg.rulesIgnored ?? 0;
      setStatus(msg.ok ? savedStatus() : '');
      refreshForm();
      for (const editor of editors) {
        editor.onSaved?.();
      }
      // ⚠ The message only clears itself when it has nothing to teach: "saved" reads at a glance,
      // while the list of ignored fields is what the user must be able to re-read in order to go
      // and fix their input.
      if (msg.ok && lastIgnored.length === 0 && lastRulesIgnored === 0) {
        setTimeout(() => setStatus(''), 2500);
      }
      break;
    case 'error':
      // A refused Save leaves the saved state as it was: the edits are still unsaved, and say so.
      if (msg.op === 'save' && savedBeforeSave) {
        config = savedBeforeSave;
        savedBeforeSave = null;
        refreshForm();
      }
      setStatus(msg.message ?? 'error');
      break;
  }
});

// Every edit recounts the unsaved changes: one listener for the whole form. A model field is
// read-only, so it changes only through its pick, which recounts too.
app.addEventListener('input', refreshForm);
app.addEventListener('change', refreshForm);

app.textContent = t('Loading settings…');
vscode.postMessage({ type: 'ready' });
