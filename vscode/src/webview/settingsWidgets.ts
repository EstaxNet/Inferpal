// The live blocks of the settings pages (`SettingSection.widget`): what the product knows NOW rather than what the
// configuration says — the code index, the project's exclusions, the @Docs sites, how full the conversation is, the
// project's files, the model a page's feature uses, the approval rules in effect. The facts come from the host,
// already localized by the Core presenters the Visual Studio window renders too; this file only draws them.
import { fill } from './l10n';
import { icon, iconButton, setIcon } from './icons';
import type {
  ApprovalRuleTable, ContextUsage, IndexCard, LoadedModels, ModelSuggestion, ProjectFileRow, SettingsDocs, SettingsExclusions,
} from '../protocol';

export interface WidgetHost {
  /** A resource of `settings/strings`, by name. */
  res(key: string): string;
  post(msg: Record<string, unknown>): void;
  /** The form's current value of a field (unsaved edits included). */
  formValue(key: string): string;
  /** Writes a field as a pick would: the form counts it as an unsaved change, nothing is saved. */
  setFormValue(key: string, value: string): void;
  /** Shows a page, and the row of a field on it. */
  showPage(page: string, fieldKey?: string): void;
  /** Whether the page holding this widget is the one shown. */
  isShown(page: string): boolean;
}

export interface LiveWidget {
  el: HTMLElement;
  /** A host answer meant for this widget: `true` when consumed. */
  onMessage?(msg: { type: string; [key: string]: unknown }): boolean;
  /** The form changed: redraw what depends on it. */
  onFormChanged?(): void;
  /** Its page was just shown: ask the host again, the facts may have moved. */
  onShown?(): void;
}

export function createWidget(name: string, page: string, host: WidgetHost): LiveWidget | null {
  switch (name) {
    case 'approvalRules':   return approvalRulesCard(host);
    case 'indexCard':       return indexCard(page, host);
    case 'indexExclusions': return indexExclusions(host);
    case 'docsSites':       return docsSites(page, host);
    case 'contextUsage':    return contextUsage(host);
    case 'projectFiles':    return projectFiles(host);
    case 'fimModel':        return modelLine(host, ['inlineCompletionModel', 'defaultModel']);
    case 'editModel':       return modelLine(host, ['inlineEditModel', 'codeActionsModel', 'defaultModel']);
    case 'themeCards':      return themeCards(host);
    case 'suggestModels':   return suggestModels(host);
    case 'loadedModels':    return loadedModels(host);
    default:                return null;
  }
}

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

function link(text: string, onClick: () => void): HTMLButtonElement {
  const btn = el('button', 'linkbtn', text);
  btn.type = 'button';
  btn.addEventListener('click', onClick);
  return btn;
}

/** "Change it in Server and models": the field that sets it, on the page that holds it. */
function serverLink(host: WidgetHost, text: string, fieldKey: string): HTMLButtonElement {
  return link(text, () => host.showPage('server', fieldKey));
}

// ── Appearance page: the editor's theme, shown and never set ──────────────────

/** The three themes as cards, the one in use marked — the panel follows the editor's, high contrast included. Each
 *  card draws its theme in that theme's own colours; the marking follows the editor when its theme changes. */
function themeCards(host: WidgetHost): LiveWidget {
  const grid = el('div', 'themecards');
  grid.setAttribute('role', 'list');
  const draw = (): void => {
    const body = document.body.classList;
    const inUse = body.contains('vscode-high-contrast') || body.contains('vscode-high-contrast-light') ? 'hc'
      : body.contains('vscode-light') ? 'light' : 'dark';
    grid.textContent = '';
    for (const [key, name] of [['light', host.res('ThemeLight')], ['dark', host.res('ThemeDark')],
      ['hc', host.res('ThemeHighContrast')]] as const) {
      const card = el('div', 'themecard ' + key + (key === inUse ? ' inuse' : ''));
      card.setAttribute('role', 'listitem');
      card.append(el('span', 'bar b1'), el('span', 'bar b2'), el('span', 'bar b3'),
        el('span', 'caption', key === inUse ? fill(host.res('ThemeInUse'), name) : name));
      grid.appendChild(card);
    }
  };
  draw();
  new MutationObserver(draw).observe(document.body, { attributes: true, attributeFilter: ['class'] });
  return { el: grid };
}

// ── Server and models page: the best installed models, and what is loaded ────

/** The model fields the suggestion reads: the three it may fill, and the advanced ones it only names. */
const SUGGESTION_KEYS = ['defaultModel', 'inlineCompletionModel', 'ragEmbeddingModel',
  'agentModel', 'codeActionsModel', 'inlineEditModel', 'utilityModel'];

/** "Suggest the best models": the host picks among the installed models and says why; the picks land in the form as
 *  unsaved changes — Save stays the user's. */
function suggestModels(host: WidgetHost): LiveWidget {
  const block = el('div', 'suggestblock');
  const button = el('button', 'secondary', host.res('BtnSuggestModels'));
  button.type = 'button';
  const head = el('div', 'blockhead');
  head.append(button, el('span', 'hint', host.res('HintSuggestModels')));
  const result = el('div', 'suggestresult');
  result.setAttribute('aria-live', 'polite');
  block.append(head, result);
  button.addEventListener('click', () => {
    button.disabled = true;
    result.textContent = '…';
    const current: Record<string, string> = {};
    for (const key of SUGGESTION_KEYS) {
      current[key] = host.formValue(key).trim();
    }
    // The form's server and card, as the refresh button sends them.
    host.post({
      type: 'suggestModels', current, vramBudgetGb: host.formValue('vramBudgetGb'),
      baseUrl: host.formValue('baseUrl'), provider: host.formValue('provider'), apiKey: host.formValue('apiKey'),
    });
  });
  return {
    el: block,
    onMessage(msg) {
      if (msg.type === 'error') {
        button.disabled = false;   // the panel shows the error itself
        result.textContent = '';
        return false;
      }
      if (msg.type !== 'suggestModels') {
        return false;
      }
      button.disabled = false;
      const s = msg.suggestion as ModelSuggestion;
      result.textContent = '';
      if (!s.refusal) {
        for (const f of s.fields) {
          if (f.changed) {
            host.setFormValue(f.key, f.value);
          }
          result.append(el('p', 'suggestline' + (f.changed ? ' changed' : ''), f.reason));
        }
        for (const note of s.notes) {
          result.append(el('p', 'cardnote', note));
        }
      }
      result.append(el('p', 'suggestsummary', s.summary));
      return true;
    },
  };
}

/** "Loaded now": what the server holds in memory, what Inferpal uses each model for, and the unload buttons. The
 *  facts and the sentence of an unload come from the host; a button only asks. */
function loadedModels(host: WidgetHost): LiveWidget {
  const block = el('div', 'loadedmodels');
  const draw = (m: LoadedModels): void => {
    block.textContent = '';
    const head = el('div', 'blockhead');
    head.append(el('span', 'loadedsummary', m.summary));
    const refresh = iconButton('retry', host.res('BtnRefreshLoaded'), 14);
    refresh.addEventListener('click', () => host.post({ type: 'loadedModels' }));
    head.append(refresh);
    if (m.canUnload && m.rows.length > 0) {
      const all = el('button', 'secondary', host.res('BtnUnloadAll'));
      all.type = 'button';
      all.addEventListener('click', () => {
        all.disabled = true;
        host.post({ type: 'unloadModels' });
      });
      head.append(all);
    }
    block.append(head);
    for (const r of m.rows) {
      const row = el('div', 'siterow');
      const main = el('div', 'lmain');
      main.append(el('code', 'lname mono', r.name), el('span', 'lsub', r.uses));
      if (r.details) {
        main.append(el('span', 'lsub hint', r.details));
      }
      row.append(main);
      if (m.canUnload) {
        const unload = el('button', 'secondary', host.res('BtnUnloadModel'));
        unload.type = 'button';
        unload.setAttribute('aria-label', `${host.res('BtnUnloadModel')} ${r.name}`);
        unload.addEventListener('click', () => {
          unload.disabled = true;
          host.post({ type: 'unloadModels', names: [r.name] });
        });
        row.append(unload);
      }
      block.append(row);
    }
    if (m.message) {
      block.append(el('p', 'cardnote', m.message));
    }
  };
  host.post({ type: 'loadedModels' });
  return {
    el: block,
    onShown: () => host.post({ type: 'loadedModels' }),
    onMessage(msg) {
      if (msg.type !== 'loadedModels') {
        return false;
      }
      draw(msg.models as LoadedModels);
      return true;
    },
  };
}

// ── Agent page: the approval rules in effect ─────────────────────────────────

/** The rules IN FORCE and where they come from, as the host's table counts them (team file included, an unreadable
 *  line or a team allow not) — the Visual Studio window reads the same table. They are edited on the Tools page. */
function approvalRulesCard(host: WidgetHost): LiveWidget {
  const card = el('div', 'summarycard');
  card.appendChild(icon('list', 16));
  const text = el('div', 'summarytext');
  const title = el('span', 'summarytitle');
  const from = el('span', 'hint');
  text.append(title, from);
  card.append(text, link(host.res('SettingsEditRules'), () => host.showPage('tools', 'permissionRules')));
  return {
    el: card,
    onMessage(msg) {
      if (msg.type !== 'rulesTable') {
        return false;
      }
      const rows = (msg.table as ApprovalRuleTable).rows.filter((r) => r.status === 'inForce');
      const team = rows.filter((r) => r.source === 'team').length;
      title.textContent = fill(host.res('SettingsApprovalRulesCount'), rows.length);
      from.textContent = fill(host.res('SettingsApprovalRulesFrom'), rows.length - team, team);
      return false;   // the rules editor reads the same answer
    },
  };
}

// ── Code search page ─────────────────────────────────────────────────────────

function indexCard(page: string, host: WidgetHost): LiveWidget {
  const card = el('div', 'statuscard');
  let polling: ReturnType<typeof setTimeout> | null = null;
  let showFiles = false;

  const draw = (c: IndexCard): void => {
    card.textContent = '';
    card.dataset.state = c.state;
    const head = el('div', 'cardhead');
    const dot = el('span', 'dot');
    dot.setAttribute('aria-hidden', 'true');
    const titles = el('div', 'cardtitles');
    titles.append(el('h2', '', c.title));
    if (c.detail) {
      titles.append(el('span', 'hint', c.detail));
    }
    head.append(dot, titles);
    if (c.state !== 'noWorkspace') {
      const rebuild = el('button', 'secondary', c.buttonLabel);
      rebuild.type = 'button';
      rebuild.disabled = !c.canRebuild;
      rebuild.addEventListener('click', () => host.post({ type: 'indexRebuild' }));
      head.append(rebuild);
    }
    card.append(head);
    for (const note of c.notes) {
      card.append(el('p', 'cardnote', note));
    }
    if (c.oversizeNote) {
      const line = el('div', 'cardnote');
      line.append(el('span', '', c.oversizeNote + ' '),
        link(host.res(showFiles ? 'IndexCardHideThem' : 'IndexCardShowThem'), () => { showFiles = !showFiles; draw(c); }));
      card.append(line);
      if (showFiles) {
        const list = el('ul', 'filelist');
        for (const f of c.oversizeFiles) {
          list.append(el('li', 'mono', f));
        }
        card.append(list);
      }
    }
    if (c.modelLine) {
      const line = el('p', 'hint');
      line.append(el('span', '', c.modelLine + ' '), serverLink(host, host.res('SettingsChangeInServer'), 'ragEmbeddingModel'));
      card.append(line);
    }
    // While a pass runs, the card follows it — only while its page is in view.
    if (polling) {
      clearTimeout(polling);
      polling = null;
    }
    if (c.state === 'indexing' && host.isShown(page)) {
      polling = setTimeout(() => host.post({ type: 'indexCard' }), 1500);
    }
  };

  return {
    el: card,
    onShown: () => host.post({ type: 'indexCard' }),
    onMessage(msg) {
      if (msg.type !== 'indexCard') {
        return false;
      }
      draw(msg.card as IndexCard);
      return true;
    },
  };
}

function indexExclusions(host: WidgetHost): LiveWidget {
  const block = el('div', 'exclusions');
  host.post({ type: 'exclusions' });
  return {
    el: block,
    onShown: () => host.post({ type: 'exclusions' }),
    onMessage(msg) {
      if (msg.type !== 'exclusions') {
        return false;
      }
      const x = msg.exclusions as SettingsExclusions;
      block.textContent = '';
      block.append(el('span', 'sublabel', host.res('IndexExclusionsTitle')));
      if (x.patterns.length === 0) {
        block.append(el('p', 'hint', host.res('IndexExclusionsHowTo')));
        return true;
      }
      const chips = el('div', 'chips');
      for (const p of x.patterns) {
        chips.append(el('code', 'chip', p));
      }
      chips.append(el('span', 'hint', host.res('IndexExclusionsFrom')));
      if (x.exists) {
        chips.append(link(host.res('SettingsEditFile'), () => host.post({ type: 'openFile', path: x.file })));
      }
      block.append(chips);
      return true;
    },
  };
}

function docsSites(page: string, host: WidgetHost): LiveWidget {
  const block = el('div', 'docsites');
  const message = el('p', 'cardnote');
  message.hidden = true;
  let adding = false;
  let polling: ReturnType<typeof setTimeout> | null = null;
  let sites: SettingsDocs['sites'] = [];

  const draw = (): void => {
    block.textContent = '';
    const head = el('div', 'blockhead');
    const add = el('button', 'secondary addbtn');
    add.type = 'button';
    setIcon(add, 'plus', host.res('DocsAddSite'), 13);
    add.addEventListener('click', () => { adding = true; draw(); });
    head.append(add);
    block.append(head);
    if (sites.length === 0 && !adding) {
      block.append(el('p', 'lempty', host.res('DocsNoSitesYet')));
    }
    for (const s of sites) {
      const row = el('div', 'siterow');
      const dot = el('span', 'dot ' + ({ indexing: 'busy', partial: 'warn', indexed: 'ok', notIndexed: '' }[s.state] ?? ''));
      dot.setAttribute('aria-hidden', 'true');
      const main = el('div', 'lmain');
      main.append(el('span', 'lname', s.title), el('code', 'lsub mono', s.address));
      const reindex = el('button', 'secondary', host.res('DocsReindex'));
      reindex.type = 'button';
      reindex.disabled = s.busy;
      reindex.setAttribute('aria-label', `${host.res('DocsReindex')} ${s.title}`);
      reindex.addEventListener('click', () => host.post({ type: 'docsAction', verb: 'reindex', arg: s.id }));
      const remove = iconButton('trash', s.removeLabel, 14);
      remove.addEventListener('click', () => host.post({ type: 'docsAction', verb: 'remove', arg: s.id }));
      row.append(dot, main, el('span', 'hint sitestatus', s.status), reindex, remove);
      block.append(row);
      if (s.holeNote) {
        block.append(el('p', 'cardnote sitenote', s.holeNote));
      }
    }
    if (adding) {
      const form = el('div', 'editform');
      const lbl = el('label', 'editfield');
      const url = el('input');
      url.type = 'text';
      url.className = 'mono';
      url.placeholder = 'https://';
      lbl.append(el('span', '', host.res('DocsAddUrlLabel')), url);
      const buttons = el('div', 'formbuttons');
      const ok = el('button', '', host.res('DocsAddButton'));
      ok.type = 'button';
      ok.addEventListener('click', () => {
        if (url.value.trim()) {
          adding = false;
          host.post({ type: 'docsAction', verb: 'add', arg: url.value.trim() });
        }
      });
      const cancel = el('button', 'secondary', host.res('SettingsCancel'));
      cancel.type = 'button';
      cancel.addEventListener('click', () => { adding = false; draw(); });
      buttons.append(ok, cancel);
      form.append(lbl, buttons);
      block.append(form);
      url.focus();
    }
    block.append(message);
  };

  host.post({ type: 'docsSites' });
  return {
    el: block,
    onShown: () => host.post({ type: 'docsSites' }),
    onMessage(msg) {
      if (msg.type !== 'docsSites') {
        return false;
      }
      const d = msg.docs as SettingsDocs;
      sites = d.sites;
      draw();
      // What the command said (added, unknown address, usage) is shown under the list until the next action.
      if (d.message) {
        message.textContent = d.message;
        message.hidden = false;
      }
      if (polling) {
        clearTimeout(polling);
        polling = null;
      }
      if (sites.some((s) => s.busy) && host.isShown(page)) {
        polling = setTimeout(() => host.post({ type: 'docsSites' }), 2000);
      }
      return true;
    },
  };
}

// ── Context page ─────────────────────────────────────────────────────────────

function contextUsage(host: WidgetHost): LiveWidget {
  const block = el('div', 'usage');
  return {
    el: block,
    onShown: () => host.post({ type: 'contextUsage' }),
    onMessage(msg) {
      if (msg.type !== 'contextUsage') {
        return false;
      }
      const u = msg.usage as ContextUsage;
      block.textContent = '';
      const head = el('div', 'usagehead');
      head.append(el('span', 'usagesum', u.summary),
        link(host.res('ContextUsageOpenXray'), () => host.post({ type: 'openXray' })));
      const bar = el('div', 'usagebar');
      bar.setAttribute('aria-hidden', 'true');
      const legend = el('div', 'usagelegend');
      for (const p of u.parts) {
        const seg = el('span', 'seg ' + p.key);
        seg.style.width = `${Math.max(0, Math.min(100, p.percent))}%`;
        bar.append(seg);
        const item = el('span', 'legenditem');
        item.append(el('span', 'swatch ' + p.key), el('span', '', `${p.label} ${p.amount}`));
        legend.append(item);
      }
      block.append(head, bar, legend,
        serverLink(host, host.res('ContextUsageChangeWindow'), 'contextWindowSize'));
      return true;
    },
  };
}

function projectFiles(host: WidgetHost): LiveWidget {
  const block = el('div', 'projectfiles');
  host.post({ type: 'projectFiles' });
  return {
    el: block,
    onShown: () => host.post({ type: 'projectFiles' }),
    onMessage(msg) {
      if (msg.type !== 'projectFiles') {
        return false;
      }
      const files = msg.files as ProjectFileRow[];
      block.textContent = '';
      if (files.length === 0) {
        return true;
      }
      block.append(el('span', 'sublabel', host.res('ProjectFilesTitle')));
      const list = el('ul', 'filerows');
      for (const f of files) {
        const li = el('li');
        li.append(el('code', 'mono', f.name), el('span', 'hint', f.description));
        li.append(f.exists
          ? link(host.res('SettingsOpenFile'), () => host.post({ type: 'openFile', path: f.fullPath }))
          : el('span', 'hint notyet', host.res('ProjectFileNotYet')));
        list.append(li);
      }
      block.append(list);
      return true;
    },
  };
}

// ── Autocomplete page: the model a feature uses, set on the Server page ──────

/** "Model: X." from the FORM — the first non-empty of the keys, the way the router resolves the role. */
function modelLine(host: WidgetHost, keys: string[]): LiveWidget {
  const line = el('p', 'hint modelline');
  const text = el('span');
  line.append(text, serverLink(host, host.res('SettingsChangeInServer'), keys[0]));
  const draw = (): void => {
    const model = keys.map((k) => host.formValue(k).trim()).find((v) => v.length > 0) ?? '';
    text.textContent = model ? fill(host.res('SettingsModelUsed'), model) + ' ' : '';
  };
  draw();
  return { el: line, onFormChanged: draw };
}
