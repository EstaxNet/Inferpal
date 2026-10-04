// The structured editors of the list settings — pinned files, MCP servers, approval rules, slash commands and
// agent tools — drawn OVER the setting's text box. The text box stays what Save writes and what the unsaved count
// reads: an editor only rewrites it. A line an editor cannot read is shown, named, and kept as written — it is
// text the user typed, and "Edit as text" is always one click away.
import { fill } from './l10n';
import { iconButton, setIcon } from './icons';
import type { ApprovalRuleTable, McpCard, McpCardsResult, PinnedFileSize, SettingsField as Field } from '../protocol';

export interface EditorHost {
  /** A resource of `settings/strings`, by name. */
  res(key: string): string;
  post(msg: Record<string, unknown>): void;
  /** Writes `text` into the setting's text box and recounts the unsaved changes. */
  commit(area: HTMLTextAreaElement, text: string): void;
}

export interface StructuredEditor {
  /** The structured view; the text box is the other one. */
  view: HTMLElement;
  /** Redraws from the text box (after "Back to the list", or a change made elsewhere). */
  refresh(): void;
  /** A host answer meant for this editor: `true` when consumed. */
  onMessage?(msg: { type: string; [key: string]: unknown }): boolean;
  /** The configuration was just saved. */
  onSaved?(): void;
}

export function createEditor(field: Field, area: HTMLTextAreaElement, host: EditorHost): StructuredEditor | null {
  switch (field.editor) {
    case 'nameValue':     return nameValueEditor(field.key, area, host, field.columns ?? []);
    case 'pinnedFiles':   return pinnedFilesEditor(area, host);
    case 'mcpServers':    return mcpServersEditor(area, host);
    case 'approvalRules': return approvalRulesEditor(area, host);
    default:              return null;
  }
}

// ── Building blocks ──────────────────────────────────────────────────────────

function el<K extends keyof HTMLElementTagNameMap>(tag: K, className?: string, text?: string): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}

function addButton(label: string): HTMLButtonElement {
  const btn = el('button', 'secondary addbtn');
  btn.type = 'button';
  setIcon(btn, 'plus', label, 13);
  return btn;
}

/** The inline add/edit form: titled, with an error line and Save/Cancel. */
function inlineForm(host: EditorHost, title: string, body: HTMLElement[], onSave: () => string | null, onCancel: () => void): HTMLElement {
  const form = el('div', 'editform');
  form.append(el('div', 'editformtitle', title), ...body);
  const error = el('p', 'editerror');
  error.setAttribute('role', 'alert');
  const actions = el('div', 'editactions');
  const save = el('button', '', host.res('BtnMcpSaveServer'));
  save.type = 'button';
  const cancel = el('button', 'secondary', host.res('BtnMcpCancelServer'));
  cancel.type = 'button';
  save.addEventListener('click', () => {
    const problem = onSave();
    error.textContent = problem ?? '';
  });
  cancel.addEventListener('click', onCancel);
  actions.append(save, cancel);
  form.append(error, actions);
  return form;
}

function labelled(label: string, input: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement): HTMLElement {
  const wrap = el('label', 'editfield');
  wrap.append(el('span', '', label), input);
  return wrap;
}

function textInput(value: string, mono = false): HTMLInputElement {
  const input = el('input', mono ? 'mono' : undefined);
  input.type = 'text';
  input.value = value;
  input.spellcheck = false;
  return input;
}

function textArea(value: string, rows = 3): HTMLTextAreaElement {
  const area = el('textarea', 'mono');
  area.rows = rows;
  area.value = value;
  area.spellcheck = false;
  return area;
}

/** A row's edit and delete buttons. */
function rowActions(host: EditorHost, onEdit: (() => void) | null, onDelete: () => void, editTitle = 'HintRowEdit', deleteTitle = 'HintRowDelete'): HTMLElement[] {
  const out: HTMLElement[] = [];
  if (onEdit) {
    const edit = iconButton('edit', host.res(editTitle), 14);
    edit.addEventListener('click', onEdit);
    out.push(edit);
  }
  const del = iconButton('trash', host.res(deleteTitle), 14);
  del.classList.add('danger');
  del.addEventListener('click', onDelete);
  out.push(del);
  return out;
}

function lines(text: string): string[] {
  return text.split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);
}

// ── name=value lists: slash commands, agent tools ────────────────────────────

const NAME_VALUE: Record<string, {
  add: string; addTitle: string; name: string; value: string; empty: string; invalid: string; duplicate: string; monoValue: boolean;
}> = {
  promptTemplates: {
    add: 'SlashAddCmd', addTitle: 'SlashAddTitle', name: 'LabelSlashName', value: 'LabelSlashText',
    empty: 'SlashEmptyTitle', invalid: 'SlashValidationNameText', duplicate: 'SlashValidationDuplicate', monoValue: false,
  },
  customTools: {
    add: 'ToolAddTool', addTitle: 'ToolAddTitle', name: 'LabelToolName', value: 'LabelToolCommand',
    empty: 'ToolEmptyTitle', invalid: 'ToolValidationNameCommand', duplicate: 'ToolValidationDuplicate', monoValue: true,
  },
};

interface NvItem { raw: string; enabled: boolean; name: string; value: string; readable: boolean }

/** One item per non-blank line, in order: a `#` prefix disables an entry, a line with no `name=value` is kept as is. */
function parseNameValue(text: string): NvItem[] {
  return lines(text).map((raw) => {
    const enabled = !raw.startsWith('#');
    const body = enabled ? raw : raw.replace(/^#+/, '').trim();
    const eq = body.indexOf('=');
    const name = eq > 0 ? body.slice(0, eq).trim() : '';
    const value = eq > 0 ? body.slice(eq + 1).trim() : '';
    return { raw, enabled, name, value, readable: name.length > 0 && value.length > 0 };
  });
}

function renderNameValue(items: NvItem[]): string {
  return items.map((i) => (i.readable ? `${i.enabled ? '' : '#'}${i.name}=${i.value}` : i.raw)).join('\n');
}

function nameValueEditor(key: string, area: HTMLTextAreaElement, host: EditorHost, columns: string[]): StructuredEditor {
  const words = NAME_VALUE[key];
  const view = el('div', 'listeditor table');
  let editing: number | 'new' | null = null;

  const refresh = (): void => {
    const items = parseNameValue(area.value);
    view.textContent = '';
    if (items.length === 0 && editing === null) {
      view.appendChild(el('p', 'lempty', host.res(words.empty)));
    } else if (items.length > 0 && columns.length === 2) {
      // The table's header: what the first column names, what the second sends or runs.
      const head = el('div', 'lrow lhead');
      head.setAttribute('aria-hidden', 'true');
      head.append(el('span', 'lcol', host.res(columns[0])), el('span', 'lcol', host.res(columns[1])));
      view.appendChild(head);
    }
    items.forEach((item, index) => {
      const row = el('div', 'lrow' + (item.readable ? '' : ' unread'));
      if (item.readable) {
        const box = el('input');
        box.type = 'checkbox';
        box.checked = item.enabled;
        box.setAttribute('aria-label', item.name);
        box.addEventListener('change', () => {
          item.enabled = box.checked;
          host.commit(area, renderNameValue(items));
        });
        row.appendChild(box);
      }
      const main = el('div', 'lmain lcols');
      if (item.readable) {
        main.append(el('span', 'lname mono', item.name), el('span', 'lsub' + (words.monoValue ? ' mono' : ''), item.value));
      } else {
        main.append(el('span', 'lname mono', item.raw), el('span', 'lnote', host.res('ListLineNotRead')));
      }
      row.appendChild(main);
      row.append(...rowActions(host, item.readable ? () => { editing = index; refresh(); } : null, () => {
        items.splice(index, 1);
        host.commit(area, renderNameValue(items));
        refresh();
      }));
      view.appendChild(row);
    });

    if (editing === null) {
      const add = addButton(host.res(words.add));
      add.addEventListener('click', () => { editing = 'new'; refresh(); });
      view.appendChild(add);
      return;
    }
    const current = editing === 'new' ? null : items[editing];
    const name = textInput(current?.name ?? '', true);
    const value = textArea(current?.value ?? '', 2);
    const title = current ? fill(host.res('RowEditTitle'), current.name) : host.res(words.addTitle);
    view.appendChild(inlineForm(host, title, [labelled(host.res(words.name), name), labelled(host.res(words.value), value)], () => {
      const n = name.value.trim();
      const v = value.value.replace(/\r?\n/g, ' ').trim();
      if (n.length === 0 || v.length === 0) {
        return host.res(words.invalid);
      }
      if (items.some((i, idx) => i.readable && idx !== editing && i.name.toLowerCase() === n.toLowerCase())) {
        return host.res(words.duplicate);
      }
      if (current) {
        current.name = n;
        current.value = v;
      } else {
        items.push({ raw: '', enabled: true, name: n, value: v, readable: true });
      }
      editing = null;
      host.commit(area, renderNameValue(items));
      refresh();
      return null;
    }, () => { editing = null; refresh(); }));
    name.focus();
  };

  refresh();
  return { view, refresh };
}

// ── Pinned files ─────────────────────────────────────────────────────────────

/** How many active pinned files reach the prompt (`PinnedFilesPolicy.MaxPinned`). */
const MAX_PINNED = 3;

function pinnedFilesEditor(area: HTMLTextAreaElement, host: EditorHost): StructuredEditor {
  const view = el('div', 'listeditor');
  let adding = false;
  let pathInput: HTMLInputElement | null = null;
  // What each file costs the prompt, asked of the host for the paths the form holds (unsaved ones included).
  const sizes = new Map<string, PinnedFileSize>();
  let asked = '';

  const refresh = (): void => {
    const items = lines(area.value).map((raw) => ({ enabled: !raw.startsWith('#'), path: raw.replace(/^#+/, '').trim() }));
    const write = (): void => host.commit(area, items.map((i) => (i.enabled ? '' : '#') + i.path).join('\n'));
    if (asked !== area.value) {
      asked = area.value;
      host.post({ type: 'pinSizes', pins: items.map((i) => i.path).join('\n') });
    }
    view.textContent = '';
    pathInput = null;
    // "2 of 3", and the gesture that adds one, above the list.
    const head = el('div', 'blockhead');
    head.appendChild(el('span', 'hint', fill(host.res('PinnedFilesCount'), Math.min(items.filter((i) => i.enabled).length, MAX_PINNED), MAX_PINNED)));
    if (!adding) {
      const add = addButton(host.res('PinnedAddFile'));
      add.addEventListener('click', () => { adding = true; refresh(); });
      head.appendChild(add);
    }
    view.appendChild(head);
    if (items.length === 0 && !adding) {
      view.appendChild(el('p', 'lempty', host.res('PinnedEmptyTitle')));
    }
    let active = 0;
    items.forEach((item, index) => {
      const row = el('div', 'lrow');
      const box = el('input');
      box.type = 'checkbox';
      box.checked = item.enabled;
      box.setAttribute('aria-label', item.path);
      box.addEventListener('change', () => { item.enabled = box.checked; write(); refresh(); });
      const main = el('div', 'lmain');
      main.appendChild(el('span', 'lname mono', item.path));
      // The cap drops what the user wrote, in silence otherwise: the prompt reads the first three only.
      if (item.enabled && ++active > MAX_PINNED) {
        main.appendChild(el('span', 'lnote', host.res('PinnedOverCap')));
      }
      const size = sizes.get(item.path);
      const cost = el('span', 'lsize' + (size?.missing ? ' warn' : ''), size?.size ?? '');
      row.append(box, main, cost, ...rowActions(host, null, () => { items.splice(index, 1); write(); refresh(); }));
      view.appendChild(row);
    });

    if (!adding) {
      return;
    }
    const input = textInput('', true);
    pathInput = input;
    const browse = el('button', 'secondary', host.res('PinnedBrowse'));
    browse.type = 'button';
    browse.addEventListener('click', () => host.post({ type: 'browsePinned' }));
    const line = el('div', 'inputline');
    line.append(input, browse);
    const field = el('label', 'editfield');
    field.append(el('span', '', host.res('LabelPinnedPath')), line);
    view.appendChild(inlineForm(host, host.res('PinnedAddTitle'), [field], () => {
      const path = input.value.trim();
      if (path.length === 0) {
        return host.res('PinnedValidationPath');
      }
      if (items.some((i) => i.path === path)) {
        return host.res('PinnedValidationDuplicate');
      }
      items.push({ enabled: true, path });
      adding = false;
      write();
      refresh();
      return null;
    }, () => { adding = false; refresh(); }));
    input.focus();
  };

  refresh();
  return {
    view,
    refresh,
    onMessage(msg) {
      if (msg.type === 'pinSizes') {
        sizes.clear();
        for (const s of msg.sizes as PinnedFileSize[]) {
          sizes.set(s.path, s);
        }
        if (!adding) {
          refresh();   // an open add form keeps what is typed in it; the sizes show on the next draw
        }
        return true;
      }
      if (msg.type !== 'pinnedPicked' || !pathInput) {
        return false;
      }
      pathInput.value = String(msg.path ?? '');
      return true;
    },
  };
}

// ── MCP servers ──────────────────────────────────────────────────────────────

type Json = Record<string, unknown>;

interface McpDoc {
  /** The object written back: the whole document. */
  doc: Json;
  /** The server map inside it (the document itself, or its `mcpServers`). */
  servers: Json;
}

/** The server map, or null when the text is not one: the list would then drop what it cannot read. */
function parseMcp(text: string): McpDoc | null {
  if (text.trim().length === 0) {
    const doc: Json = {};
    return { doc, servers: doc };
  }
  try {
    const parsed: unknown = JSON.parse(text);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return null;
    }
    const doc = parsed as Json;
    const wrapped = doc['mcpServers'];
    if (wrapped && typeof wrapped === 'object' && !Array.isArray(wrapped)) {
      return { doc, servers: wrapped as Json };
    }
    return { doc, servers: doc };
  } catch {
    return null;
  }
}

function stringList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

function stringMap(value: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    for (const [k, v] of Object.entries(value as Json)) {
      if (typeof v === 'string') {
        out[k] = v;
      }
    }
  }
  return out;
}

function mapText(map: Record<string, string>): string {
  return Object.entries(map).map(([k, v]) => `${k}=${v}`).join('\n');
}

function parseMapText(text: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const line of lines(text)) {
    const eq = line.indexOf('=');
    if (eq > 0) {
      out[line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
    }
  }
  return out;
}

/** The command line as typed: an argument with a space is quoted (`McpServerCards.CommandLine`). */
function commandLine(def: Json): string {
  const command = typeof def['command'] === 'string' ? def['command'] : '';
  return [command, ...stringList(def['args']).map((a) => (a.includes(' ') ? `"${a}"` : a))].join(' ').trim();
}

function mcpServersEditor(area: HTMLTextAreaElement, host: EditorHost): StructuredEditor {
  const view = el('div', 'listeditor mcpeditor');
  let editing: string | null | undefined;      // undefined: no form; null: adding; a name: editing it
  let last: McpCardsResult | null = null;
  const busy = new Set<string>();

  const refresh = (): void => {
    view.textContent = '';
    const parsed = parseMcp(area.value);
    if (!parsed) {
      // Rewriting the list from what it read would drop the rest: the text view is the only safe editor.
      view.appendChild(el('p', 'warn', host.res('McpJsonNotEditableAsList')));
      return;
    }
    const write = (): void => {
      host.commit(area, Object.keys(parsed.doc).length === 0 ? '' : JSON.stringify(parsed.doc, null, 2));
      refresh();
    };

    if (last?.summary) {
      view.appendChild(el('p', 'mcpsummary', last.summary));
    }
    if (last?.error) {
      view.appendChild(el('p', 'warn', last.error));
    }
    const byName = new Map<string, McpCard>((last?.cards ?? []).map((c) => [c.name, c]));
    const names = Object.keys(parsed.servers);
    if (names.length === 0 && editing === undefined) {
      view.appendChild(el('p', 'lempty', host.res('McpEmptyTitle')));
    }

    for (const name of names) {
      const raw = parsed.servers[name];
      const def: Json = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw as Json : {};
      const http = typeof def['url'] === 'string';
      const enabled = def['disabled'] !== true;
      const transport = http ? 'HTTP' : typeof def['command'] === 'string' ? 'stdio' : '';
      const target = http ? String(def['url']) : commandLine(def);
      const status = byName.get(name);
      const state = !enabled ? 'off' : status && status.state !== 'off' ? status.state : 'notStarted';
      const statusText = !enabled ? host.res('McpCardOff')
        : status && status.state !== 'off' ? status.statusText
        : host.res('McpCardNotStarted');

      const card = el('div', 'mcpcard state-' + state);
      const row = el('div', 'lrow');
      const dot = el('span', 'dot');
      dot.setAttribute('aria-hidden', 'true');
      const main = el('div', 'lmain');
      main.appendChild(el('span', 'lname', name));
      if (transport) {
        const sub = el('span', 'lsub');
        sub.append(`${transport} · `, el('code', '', target));
        main.appendChild(sub);
      }
      row.append(dot, main, el('span', 'mcpstatus', statusText));

      if (enabled && (state === 'signIn' || state === 'failed')) {
        const action = el('button', state === 'signIn' ? '' : 'secondary',
          busy.has(name) ? '…' : host.res(state === 'signIn' ? 'McpCardSignInButton' : 'McpCardRetry'));
        action.type = 'button';
        action.disabled = busy.has(name);
        action.addEventListener('click', () => {
          busy.add(name);
          host.post(state === 'signIn' ? { type: 'mcpAuthorize', name } : { type: 'mcpRetry' });
          refresh();
        });
        row.appendChild(action);
      }

      const toggle = el('input');
      toggle.type = 'checkbox';
      toggle.checked = enabled;
      toggle.setAttribute('aria-label', name);
      toggle.addEventListener('change', () => {
        if (toggle.checked) {
          delete def['disabled'];
        } else {
          def['disabled'] = true;
        }
        parsed.servers[name] = def;
        write();
      });
      row.appendChild(toggle);
      row.append(...rowActions(host, () => { editing = name; refresh(); }, () => {
        delete parsed.servers[name];
        write();
      }, 'HintMcpEditServer', 'HintMcpDeleteServer'));
      card.appendChild(row);
      // The cause is the server's own words (its stderr, the refusal): what the user needs to fix it.
      if (enabled && status?.cause) {
        card.appendChild(el('code', 'mcpcause', status.cause));
      }
      view.appendChild(card);
    }

    if (editing === undefined) {
      const add = addButton(host.res('McpAddServer'));
      add.addEventListener('click', () => { editing = null; refresh(); });
      view.appendChild(add);
      return;
    }

    const originalName = editing;
    const source: Json = originalName !== null && parsed.servers[originalName] && typeof parsed.servers[originalName] === 'object'
      ? { ...(parsed.servers[originalName] as Json) } : {};
    const name = textInput(originalName ?? '', true);
    const isHttp = el('input');
    isHttp.type = 'checkbox';
    isHttp.checked = typeof source['url'] === 'string';
    const httpLabel = el('label', 'editcheck');
    httpLabel.append(isHttp, el('span', '', host.res('LabelMcpHttpServer')));
    const command = textInput(typeof source['command'] === 'string' ? source['command'] : '', true);
    const args = textArea(stringList(source['args']).join('\n'), 3);
    const env = textArea(mapText(stringMap(source['env'])), 2);
    const url = textInput(typeof source['url'] === 'string' ? source['url'] : '', true);
    const headers = textArea(mapText(stringMap(source['headers'])), 2);
    const stdioPart = el('div');
    stdioPart.append(labelled(host.res('LabelMcpCommand'), command), labelled(host.res('LabelMcpArgs'), args), labelled(host.res('LabelMcpEnv'), env));
    const httpPart = el('div');
    httpPart.append(labelled(host.res('LabelMcpUrl'), url), labelled(host.res('LabelMcpHeaders'), headers));
    const sync = (): void => { stdioPart.hidden = isHttp.checked; httpPart.hidden = !isHttp.checked; };
    isHttp.addEventListener('change', sync);
    sync();

    const title = originalName === null ? host.res('McpAddTitle') : fill(host.res('McpEditTitle'), originalName);
    view.appendChild(inlineForm(host, title, [labelled(host.res('LabelMcpName'), name), httpLabel, stdioPart, httpPart], () => {
      const n = name.value.trim();
      if (isHttp.checked ? n.length === 0 || url.value.trim().length === 0 : n.length === 0 || command.value.trim().length === 0) {
        return host.res(isHttp.checked ? 'McpValidationNameUrl' : 'McpValidationNameCommand');
      }
      if (n !== originalName && Object.prototype.hasOwnProperty.call(parsed.servers, n)) {
        return host.res('McpValidationDuplicate');
      }
      // What the form does not show (oauth, keys of other tools) is kept: the entry is edited, not rebuilt.
      const def: Json = { ...source };
      if (isHttp.checked) {
        delete def['command']; delete def['args']; delete def['env'];
        def['url'] = url.value.trim();
        const h = parseMapText(headers.value);
        if (Object.keys(h).length > 0) { def['headers'] = h; } else { delete def['headers']; }
      } else {
        delete def['url']; delete def['headers']; delete def['oauth'];
        def['command'] = command.value.trim();
        const a = lines(args.value);
        if (a.length > 0) { def['args'] = a; } else { delete def['args']; }
        const e = parseMapText(env.value);
        if (Object.keys(e).length > 0) { def['env'] = e; } else { delete def['env']; }
      }
      // A renamed server keeps its place in the list.
      const entries = Object.entries(parsed.servers);
      for (const k of Object.keys(parsed.servers)) {
        delete parsed.servers[k];
      }
      let placed = false;
      for (const [k, v] of entries) {
        if (k === originalName) {
          parsed.servers[n] = def;
          placed = true;
        } else {
          parsed.servers[k] = v;
        }
      }
      if (!placed) {
        parsed.servers[n] = def;
      }
      editing = undefined;
      write();
      return null;
    }, () => { editing = undefined; refresh(); }));
    name.focus();
  };

  refresh();
  host.post({ type: 'mcpCards' });
  return {
    view,
    refresh,
    onMessage(msg) {
      if (msg.type === 'mcpCards') {
        last = (msg.result as McpCardsResult | undefined) ?? null;
        busy.clear();
        refresh();
        return true;
      }
      if (msg.type === 'error' && busy.size > 0) {
        busy.clear();
        refresh();      // the error itself is shown by the panel's status line
      }
      return false;
    },
    onSaved() {
      // The host restarts the servers when their settings change, inside the save: the cards it sends now are new.
      host.post({ type: 'mcpCards' });
    },
  };
}

// ── Approval rules ───────────────────────────────────────────────────────────

function approvalRulesEditor(area: HTMLTextAreaElement, host: EditorHost): StructuredEditor {
  const view = el('div', 'listeditor ruleseditor');
  let table: ApprovalRuleTable | null = null;
  let request = 0;
  let pendingAdd = 0;
  let addError = '';
  const draft = { allow: false, tool: '', pattern: '' };

  const ask = (): void => {
    request += 1;
    host.post({ type: 'rulesTable', rules: area.value, requestId: request });
  };

  const draw = (): void => {
    view.textContent = '';
    // The order, the team file's contract and the denylist are said once, by the section's description.
    if (table?.teamUnusable) {
      view.appendChild(el('p', 'warn', host.res('RulesTeamUnusable')));
    }
    const rows = table?.rows ?? [];
    if (rows.length === 0) {
      view.appendChild(el('p', 'lempty', host.res('RulesEmpty')));
    } else {
      const t = el('table', 'rules');
      const head = el('tr');
      for (const key of ['RulesColEffect', 'RulesColTool', 'RulesColPattern', 'RulesColFrom']) {
        head.appendChild(el('th', '', host.res(key)));
      }
      head.appendChild(el('th'));
      t.appendChild(el('thead')).appendChild(head);
      const body = el('tbody');
      for (const r of rows) {
        const tr = el('tr', 'status-' + r.status);
        const effect = el('td');
        if (r.allow !== null && r.allow !== undefined) {
          effect.appendChild(el('span', 'pill ' + (r.allow ? 'allow' : 'deny'), r.effectText));
        }
        const tool = el('td');
        if (r.tool) {
          tool.appendChild(el('code', '', r.tool));
        }
        const pattern = el('td');
        pattern.appendChild(el('code', '', r.pattern));
        if (r.noteText) {
          pattern.appendChild(el('span', 'lnote', r.noteText));
        }
        const actions = el('td', 'ractions');
        if (r.source === 'machine' && r.machineLine >= 0) {
          actions.append(...rowActions(host, null, () => {
            const all = area.value.split('\n');
            all.splice(r.machineLine, 1);
            host.commit(area, all.join('\n'));
            ask();
          }));
        }
        tr.append(effect, tool, pattern, el('td', 'rfrom', r.fromText), actions);
        body.appendChild(tr);
      }
      t.appendChild(body);
      view.appendChild(t);
    }

    // A new rule: written only if the host would read it — a rule that is ignored is a restriction believed set.
    const form = el('div', 'ruleadd');
    const effect = el('select');
    for (const [value, key] of [['deny', 'RuleDeny'], ['allow', 'RuleAllow']]) {
      const o = el('option', '', host.res(key));
      o.value = value;
      o.selected = (value === 'allow') === draft.allow;
      effect.appendChild(o);
    }
    effect.setAttribute('aria-label', host.res('RulesColEffect'));
    const tool = textInput(draft.tool, true);
    tool.placeholder = '*';
    tool.setAttribute('aria-label', host.res('RulesColTool'));
    const pattern = textInput(draft.pattern, true);
    pattern.placeholder = host.res('RulesPatternPlaceholder');
    pattern.setAttribute('aria-label', host.res('RulesColPattern'));
    const keep = (): void => { draft.allow = effect.value === 'allow'; draft.tool = tool.value; draft.pattern = pattern.value; };
    for (const input of [effect, tool, pattern]) {
      input.addEventListener('input', keep);
      input.addEventListener('change', keep);
    }
    const add = addButton(host.res('RulesAddRule'));
    add.addEventListener('click', () => {
      keep();
      pendingAdd = ++request;
      host.post({ type: 'newRule', allow: draft.allow, tool: draft.tool, pattern: draft.pattern, requestId: pendingAdd });
    });
    form.append(effect, tool, pattern, add);
    view.appendChild(form);
    if (addError) {
      view.appendChild(el('p', 'editerror', addError));
    }
  };

  draw();
  ask();
  return {
    view,
    refresh: ask,
    onMessage(msg) {
      if (msg.type === 'rulesTable') {
        if (msg.requestId === request) {
          table = msg.table as ApprovalRuleTable;
          draw();
        }
        return true;
      }
      if (msg.type === 'newRule') {
        if (msg.requestId !== pendingAdd) {
          return true;
        }
        const line = msg.line as string | null;
        if (!line) {
          addError = host.res('RulesNewInvalid');
          draw();
          return true;
        }
        addError = '';
        draft.tool = '';
        draft.pattern = '';
        const text = area.value.trimEnd();
        host.commit(area, text.length === 0 ? line : `${text}\n${line}`);
        ask();
        return true;
      }
      return false;
    },
  };
}
