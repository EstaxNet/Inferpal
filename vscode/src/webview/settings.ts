// Inferpal Settings webview: mirrors the Visual Studio settings window — same tabs, same
// sections, same labels/hints (served localized by the host from the shared .resx via
// `settings/strings`), ⓘ tooltips, compact numeric fields with unit suffixes, the
// "distinct model per role" and "advanced settings" reveal toggles, and Test / refresh
// buttons. The config JSON is mutated in place and saved WHOLE (config/update replaces
// the config — absent fields would reset).
import { t } from './l10n';

const vscode = acquireVsCodeApi();

// Localized resources pushed by the extension (resx resource name → translated string).
let R: Record<string, string> = {};
const res = (key: string): string => R[key] ?? key;

type FieldType = 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';

interface Field {
  /** camelCase key in the config JSON. */
  key: string;
  type: FieldType;
  /** resx resource names for the label and the ⓘ tooltip. */
  label: string;
  hint?: string;
  /** Unit suffix rendered after compact numeric fields (literal, like VS). */
  unit?: string;
  options?: { value: string; text: string }[];
  /** UI-only reveal group: 'roles' (distinct model per role) or 'advanced' (behavior). */
  gate?: 'roles' | 'advanced';
  /** Companion button: 'test' (connection check) or 'refreshModels'. */
  button?: 'test' | 'refreshModels';
}

interface Section {
  title: string; // resx resource name
  fields: Field[];
  /** Reveal-toggle rendered at the top of the section (label = resx name). */
  toggle?: { gate: 'roles' | 'advanced'; label: string; hint?: string };
}

const FIM_MODES = [
  { value: 'Fast', text: 'Fast (128 tok · 300 ms)' },
  { value: 'Default', text: 'Default (256 tok · 600 ms)' },
  { value: 'HighAccuracy', text: 'High Accuracy (512 tok · 1 s)' },
];

// Fixed like the VS list — language names are deliberately never localized.
const LANGUAGES = [
  { value: '', text: 'Auto' },
  { value: 'en', text: 'English' }, { value: 'fr', text: 'Français' },
  { value: 'de', text: 'Deutsch' }, { value: 'es', text: 'Español' },
  { value: 'it', text: 'Italiano' }, { value: 'ru', text: 'Русский' },
  { value: 'ja', text: '日本語' }, { value: 'ko', text: '한국어' },
  { value: 'pl', text: 'Polski' }, { value: 'zh-CN', text: '中文(简体)' },
];

const TABS: { key: string; title: string; sections: Section[] }[] = [
  {
    key: 'connection',
    title: 'SectionConnection',
    sections: [
      {
        title: 'SectionConnection',
        fields: [
          {
            key: 'provider', type: 'select', label: 'LabelProvider', hint: 'HintProvider',
            options: [
              { value: 'ollama', text: 'Ollama' },
              { value: 'lmstudio', text: 'LM Studio' },
              { value: 'openai-compatible', text: 'OpenAI-compatible' },
            ],
          },
          { key: 'baseUrl', type: 'text', label: 'LabelUrl', hint: 'HintUrl', button: 'test' },
          { key: 'apiKey', type: 'password', label: 'LabelApiKey', hint: 'HintApiKey' },
          { key: 'defaultModel', type: 'model', label: 'LabelChatModel', hint: 'HintChatModel', button: 'refreshModels' },
        ],
      },
      {
        title: '',
        toggle: { gate: 'roles', label: 'LabelModelRolesAdvanced', hint: 'HintModelRolesAdvanced' },
        fields: [
          { key: 'agentModel', type: 'model', label: 'LabelAgentModel', hint: 'HintAgentModel', gate: 'roles' },
          { key: 'codeActionsModel', type: 'model', label: 'LabelCodeActionsModel', hint: 'HintCodeActionsModel', gate: 'roles' },
          { key: 'inlineCompletionModel', type: 'model', label: 'LabelInlineCompletionModel', hint: 'HintInlineCompletionModel', gate: 'roles' },
          { key: 'inlineEditModel', type: 'model', label: 'LabelInlineEditModel', hint: 'HintInlineEditModel', gate: 'roles' },
          { key: 'utilityModel', type: 'model', label: 'LabelUtilityModel', hint: 'HintUtilityModel', gate: 'roles' },
          { key: 'modelRouterAuto', type: 'bool', label: 'LabelModelRouterAuto', hint: 'HintModelRouterAuto', gate: 'roles' },
          { key: 'ragEmbeddingModel', type: 'model', label: 'LabelRagEmbeddingModel', hint: 'HintRagEmbeddingModel' },
        ],
      },
    ],
  },
  {
    key: 'behavior',
    title: 'SectionBehavior',
    sections: [
      {
        title: 'SectionBehavior',
        toggle: { gate: 'advanced', label: 'LabelAdvancedBehavior' },
        fields: [
          { key: 'commandTimeoutSeconds', type: 'int', label: 'LabelCommandTimeout', hint: 'HintCommandTimeout', unit: 's', gate: 'advanced' },
          { key: 'quickTimeoutSeconds', type: 'int', label: 'LabelTaskTimeoutQuick', hint: 'HintTaskTimeoutQuick', unit: 's', gate: 'advanced' },
          { key: 'normalTimeoutSeconds', type: 'int', label: 'LabelTaskTimeoutNormal', hint: 'HintTaskTimeoutNormal', unit: 's', gate: 'advanced' },
          { key: 'deepTimeoutSeconds', type: 'int', label: 'LabelTaskTimeoutDeep', hint: 'HintTaskTimeoutDeep', unit: 's', gate: 'advanced' },
          { key: 'agentMaxIterations', type: 'int', label: 'LabelAgentMaxIterations', hint: 'HintAgentMaxIterations', gate: 'advanced' },
          { key: 'modelAutoUnloadEnabled', type: 'bool', label: 'LabelModelAutoUnload', hint: 'HintModelAutoUnload', gate: 'advanced' },
          { key: 'modelIdleTimeoutMinutes', type: 'int', label: 'LabelModelIdleTimeout', hint: 'HintModelIdleTimeout', unit: 'min', gate: 'advanced' },
          { key: 'toolBubblesExpanded', type: 'bool', label: 'LabelToolBubblesExpanded', hint: 'HintToolBubblesExpanded' },
          { key: 'securityAlertsDisabled', type: 'bool', label: 'LabelSecurityAlertsDisabled', hint: 'HintSecurityAlertsDisabled' },
          { key: 'permissionRules', type: 'textarea', label: 'LabelPermissionRules', hint: 'HintPermissionRules' },
          { key: 'smartFixEnabled', type: 'bool', label: 'LabelSmartFixEnabled', hint: 'HintSmartFixEnabled' },
          { key: 'agentModeEnabled', type: 'bool', label: 'LabelAgentModeEnabled', hint: 'HintAgentModeEnabled' },
        ],
      },
      {
        title: 'SectionInlineCompletions',
        fields: [
          { key: 'inlineCompletionEnabled', type: 'bool', label: 'LabelInlineCompletionEnabled', hint: 'HintInlineCompletionEnabled' },
          { key: 'inlineCompletionMode', type: 'select', label: 'LabelInlineCompletionMode', hint: 'HintInlineCompletionMode', options: FIM_MODES },
        ],
      },
      {
        title: 'SectionPersona',
        fields: [
          { key: 'personaAutoSwitch', type: 'bool', label: 'LabelPersonaAutoSwitch', hint: 'HintPersonaAutoSwitch' },
          { key: 'customSystemPrompt', type: 'textarea', label: 'LabelCustomSystemPrompt', hint: 'HintCustomSystemPrompt' },
        ],
      },
    ],
  },
  {
    key: 'context',
    title: 'SectionContext',
    sections: [
      {
        title: 'SectionRag',
        fields: [
          { key: 'ragEnabled', type: 'bool', label: 'LabelRagEnabled', hint: 'HintRagEnabled' },
          { key: 'ragAutoContextEnabled', type: 'bool', label: 'LabelRagAutoContext', hint: 'HintRagAutoContext' },
          { key: 'ragTopK', type: 'int', label: 'LabelRagTopK', hint: 'HintRagTopK', unit: 'chunks' },
          { key: 'ragSimilarityThreshold', type: 'float', label: 'LabelRagSimilarityThreshold', hint: 'HintRagSimilarityThreshold', unit: '0–1' },
          { key: 'lspEnabled', type: 'bool', label: 'LabelLspEnabled', hint: 'HintLspEnabled' },
        ],
      },
      {
        title: 'SectionContext',
        fields: [
          { key: 'vramBudgetGb', type: 'float', label: 'LabelVramBudget', hint: 'HintVramBudget', unit: 'GB' },
          { key: 'contextWindowSize', type: 'int', label: 'LabelContextWindowSize', hint: 'HintContextWindowSize', unit: 'tokens' },
          { key: 'contextWindowKeepTurns', type: 'int', label: 'LabelContextWindowKeepTurns', hint: 'HintContextWindowKeepTurns', unit: 'turns' },
          { key: 'compactionEnabled', type: 'bool', label: 'LabelCompactionEnabled', hint: 'HintCompactionEnabled' },
          { key: 'compactionTimeoutSeconds', type: 'int', label: 'LabelCompactionTimeout', hint: 'HintCompactionTimeout', unit: 's' },
          { key: 'kvCacheAnchorMessages', type: 'int', label: 'LabelKvCacheAnchor', hint: 'HintKvCacheAnchor', unit: 'msg' },
          { key: 'oodaTurnThreshold', type: 'int', label: 'LabelOodaTurnThreshold', hint: 'HintOodaTurnThreshold', unit: 'turns' },
          { key: 'inlineDiffPreviewEnabled', type: 'bool', label: '__inlineDiffPreview', hint: undefined },
          { key: 'pinnedContextFiles', type: 'textarea', label: 'LabelPinnedContextFiles', hint: 'HintPinnedContextFiles' },
        ],
      },
    ],
  },
  {
    key: 'tools',
    title: '__tabTools',
    sections: [
      {
        title: 'SectionMcp',
        fields: [
          { key: 'mcpEnabled', type: 'bool', label: 'LabelMcpEnabled', hint: 'HintMcpEnabled' },
          { key: 'mcpServersJson', type: 'textarea', label: 'LabelMcpServers', hint: 'HintMcpServers' },
        ],
      },
      {
        title: 'SectionCommandsTools',
        fields: [
          { key: 'promptTemplates', type: 'textarea', label: 'LabelPromptTemplates', hint: 'HintPromptTemplates' },
          { key: 'customTools', type: 'textarea', label: 'LabelCustomTools', hint: 'HintCustomTools' },
        ],
      },
    ],
  },
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

function infoIcon(hintKey?: string): HTMLElement | null {
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
  TABS.forEach((tab, i) => {
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

  const modelList = document.createElement('datalist');
  modelList.id = 'models';
  renderModelOptions(modelList);
  app.appendChild(modelList);

  // Initial reveal state, like VS: roles shown when any role field is set.
  gateOn.roles = ['agentModel', 'codeActionsModel', 'inlineCompletionModel', 'inlineEditModel', 'utilityModel']
    .some((key) => String(config[key] ?? '').length > 0) || config['modelRouterAuto'] === true;
  gateOn.advanced = false;

  TABS.forEach((tab, i) => {
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
      if (section.toggle) {
        page.appendChild(renderToggle(section.toggle));
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

function renderModelOptions(list: HTMLElement): void {
  list.textContent = '';
  for (const name of models) {
    const opt = document.createElement('option');
    opt.value = name;
    list.appendChild(opt);
  }
}

function renderField(field: Field): HTMLElement {
  const row = document.createElement('div');
  const compact = field.type === 'int' || field.type === 'float';
  row.className = 'field' + (field.type === 'bool' ? ' inline' : compact ? ' compact' : '');
  if (field.gate) {
    row.dataset.gate = field.gate;
  }

  const lbl = document.createElement('label');
  lbl.textContent = label(field);
  lbl.htmlFor = 'f-' + field.key;
  const icon = infoIcon(field.hint);

  let input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
  const value = config[field.key];
  switch (field.type) {
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
      box.type = field.type === 'password' ? 'password' : 'text';
      if (field.type === 'model') {
        box.setAttribute('list', 'models');
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

  if (field.type === 'bool') {
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
  if (field.button) {
    const line = document.createElement('div');
    line.className = 'inputline';
    line.append(input);
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
  const allFields: Field[] = TABS.flatMap((tab) => tab.sections.flatMap((s) => s.fields));
  allFields.push({ key: 'language', type: 'select', label: 'LabelLanguage' });
  for (const field of allFields) {
    const input = inputs.get(field.key);
    if (!input) {
      continue;
    }
    switch (field.type) {
      case 'bool':
        config[field.key] = (input as HTMLInputElement).checked;
        break;
      case 'int': {
        const n = parseInt(input.value, 10);
        if (!Number.isNaN(n)) {
          config[field.key] = n;
        }
        break;
      }
      case 'float': {
        const n = parseFloat(input.value.replace(',', '.'));
        if (!Number.isNaN(n)) {
          config[field.key] = n;
        }
        break;
      }
      default:
        config[field.key] = input.value;
        break;
    }
  }
  vscode.postMessage({ type: 'save', json: JSON.stringify(config, null, 2) });
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
    message?: string; ok?: boolean;
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
      render();
      break;
    case 'models': {
      models = msg.models ?? [];
      const list = document.getElementById('models');
      if (list) {
        renderModelOptions(list);
      }
      break;
    }
    case 'testResult':
      setTestStatus(msg.ok ? t('Connected') : t('Backend unreachable'), msg.ok);
      break;
    case 'saveDone':
      setStatus(msg.ok ? t('Settings saved.') : '');
      if (msg.ok) {
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
