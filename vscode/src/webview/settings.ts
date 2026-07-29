// Inferpal Settings webview: renders the 4 tabs of the VS settings window from the
// host's config JSON and saves it back WHOLE (config/update replaces the config — the
// parsed original object is mutated in place so unknown/unedited fields survive).
import { t } from './l10n';

const vscode = acquireVsCodeApi();

type FieldType = 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';

interface Field {
  /** camelCase key in the config JSON + l10n label key. */
  key: string;
  type: FieldType;
  options?: string[];
  /** Renders inside the collapsible "advanced model roles" group. */
  advanced?: boolean;
}

const TABS: { key: string; fields: Field[] }[] = [
  {
    key: 'tabConnection',
    fields: [
      { key: 'provider', type: 'select', options: ['ollama', 'lmstudio', 'openai'] },
      { key: 'baseUrl', type: 'text' },
      { key: 'apiKey', type: 'password' },
      { key: 'defaultModel', type: 'model' },
      { key: 'agentModel', type: 'model', advanced: true },
      { key: 'codeActionsModel', type: 'model', advanced: true },
      { key: 'inlineCompletionModel', type: 'model', advanced: true },
      { key: 'inlineEditModel', type: 'model', advanced: true },
      { key: 'utilityModel', type: 'model', advanced: true },
      { key: 'modelRouterAuto', type: 'bool', advanced: true },
      { key: 'ragEmbeddingModel', type: 'model', advanced: true },
    ],
  },
  {
    key: 'tabBehavior',
    fields: [
      { key: 'commandTimeoutSeconds', type: 'int' },
      { key: 'quickTimeoutSeconds', type: 'int' },
      { key: 'normalTimeoutSeconds', type: 'int' },
      { key: 'deepTimeoutSeconds', type: 'int' },
      { key: 'agentMaxIterations', type: 'int' },
      { key: 'toolBubblesExpanded', type: 'bool' },
      { key: 'securityAlertsDisabled', type: 'bool' },
      { key: 'smartFixEnabled', type: 'bool' },
      { key: 'inlineCompletionEnabled', type: 'bool' },
      { key: 'inlineDiffPreviewEnabled', type: 'bool' },
      { key: 'modelAutoUnloadEnabled', type: 'bool' },
      { key: 'modelIdleTimeoutMinutes', type: 'int' },
      { key: 'permissionRules', type: 'textarea' },
    ],
  },
  {
    key: 'tabContext',
    fields: [
      { key: 'vramBudgetGb', type: 'float' },
      { key: 'contextWindowSize', type: 'int' },
      { key: 'contextWindowKeepTurns', type: 'int' },
      { key: 'compactionEnabled', type: 'bool' },
      { key: 'compactionTimeoutSeconds', type: 'int' },
      { key: 'kvCacheAnchorMessages', type: 'int' },
      { key: 'oodaTurnThreshold', type: 'int' },
      { key: 'personaAutoSwitch', type: 'bool' },
      { key: 'customSystemPrompt', type: 'textarea' },
      { key: 'pinnedContextFiles', type: 'textarea' },
    ],
  },
  {
    key: 'tabTools',
    fields: [
      { key: 'ragEnabled', type: 'bool' },
      { key: 'ragAutoContextEnabled', type: 'bool' },
      { key: 'ragTopK', type: 'int' },
      { key: 'ragSimilarityThreshold', type: 'float' },
      { key: 'lspEnabled', type: 'bool' },
      { key: 'mcpEnabled', type: 'bool' },
      { key: 'mcpServersJson', type: 'textarea' },
      { key: 'promptTemplates', type: 'textarea' },
      { key: 'customTools', type: 'textarea' },
    ],
  },
];

const app = document.getElementById('app')!;
let config: Record<string, unknown> = {};
let models: string[] = [];
const inputs = new Map<string, HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>();
let statusEl: HTMLElement | null = null;

function render(): void {
  app.textContent = '';
  inputs.clear();

  const tabbar = document.createElement('div');
  tabbar.id = 'tabbar';
  const pages: HTMLElement[] = [];
  TABS.forEach((tab, i) => {
    const btn = document.createElement('button');
    btn.className = 'tab' + (i === 0 ? ' active' : '');
    btn.textContent = t(tab.key);
    btn.addEventListener('click', () => {
      tabbar.querySelectorAll('.tab').forEach((b, j) => b.classList.toggle('active', j === i));
      pages.forEach((pg, j) => { pg.hidden = j !== i; });
    });
    tabbar.appendChild(btn);
  });
  app.appendChild(tabbar);

  const modelList = document.createElement('datalist');
  modelList.id = 'models';
  for (const name of models) {
    const opt = document.createElement('option');
    opt.value = name;
    modelList.appendChild(opt);
  }
  app.appendChild(modelList);

  TABS.forEach((tab, i) => {
    const page = document.createElement('div');
    page.className = 'page';
    page.hidden = i !== 0;

    const plain = tab.fields.filter((f) => !f.advanced);
    const advanced = tab.fields.filter((f) => f.advanced);
    for (const field of plain) {
      page.appendChild(renderField(field));
    }
    if (advanced.length > 0) {
      const details = document.createElement('details');
      const summary = document.createElement('summary');
      summary.textContent = t('advancedRoles');
      details.appendChild(summary);
      for (const field of advanced) {
        details.appendChild(renderField(field));
      }
      page.appendChild(details);
    }
    pages.push(page);
    app.appendChild(page);
  });

  const footer = document.createElement('div');
  footer.id = 'footer';
  const save = document.createElement('button');
  save.id = 'save';
  save.textContent = t('save');
  save.addEventListener('click', onSave);
  statusEl = document.createElement('span');
  statusEl.id = 'status';
  footer.append(save, statusEl);
  app.appendChild(footer);
}

function renderField(field: Field): HTMLElement {
  const row = document.createElement('div');
  row.className = 'field' + (field.type === 'bool' ? ' inline' : '');
  const label = document.createElement('label');
  label.textContent = t(field.key);
  label.htmlFor = 'f-' + field.key;

  let input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
  const value = config[field.key];
  switch (field.type) {
    case 'select': {
      const select = document.createElement('select');
      for (const opt of field.options ?? []) {
        const o = document.createElement('option');
        o.value = opt;
        o.textContent = opt;
        o.selected = String(value ?? '') === opt;
        select.appendChild(o);
      }
      input = select;
      break;
    }
    case 'textarea': {
      const area = document.createElement('textarea');
      area.rows = 5;
      area.value = String(value ?? '');
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
      box.value = String(value ?? '');
      input = box;
      break;
    }
  }
  input.id = 'f-' + field.key;
  inputs.set(field.key, input);

  if (field.type === 'bool') {
    row.append(input, label);
  } else {
    row.append(label, input);
  }
  return row;
}

function onSave(): void {
  // Mutate the parsed original so fields this form doesn't know about survive the
  // full-JSON round trip (config/update resets absent fields to their defaults).
  for (const tab of TABS) {
    for (const field of tab.fields) {
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
  const msg = event.data as { type: string; configJson?: string; models?: string[]; message?: string; ok?: boolean };
  switch (msg.type) {
    case 'init':
      try {
        config = JSON.parse(msg.configJson ?? '{}') as Record<string, unknown>;
      } catch {
        config = {};
      }
      models = msg.models ?? [];
      render();
      break;
    case 'saveDone':
      setStatus(msg.ok ? t('saved') : '');
      if (msg.ok) {
        setTimeout(() => setStatus(''), 2500);
      }
      break;
    case 'error':
      setStatus(msg.message ?? 'error');
      break;
  }
});

app.textContent = t('loading');
vscode.postMessage({ type: 'ready' });
