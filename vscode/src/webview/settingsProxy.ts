// The proxy access block of the Server and models page, drawn over the `requestHeaders` text box: why a proxy is there,
// the method it asks for, the method's fields — Inferpal builds the headers from them —, what will be sent and to whom,
// and the old proxy's credentials left behind when the address moves. The text box stays what Save writes; this block
// only rewrites it. The Core reads the same text with the same rule (`ProxyAccess.Read` / `Write`).
import { fill } from './l10n';
import type { EditorHost, StructuredEditor } from './settingsEditors';

export type ProxyMethod = 'none' | 'basic' | 'bearer' | 'cloudflare' | 'custom';

export interface ProxyForm {
  method: ProxyMethod;
  user: string;
  password: string;
  token: string;
  clientId: string;
  clientSecret: string;
  raw: string;
}

const CF_ID = 'CF-Access-Client-Id';
const CF_SECRET = 'CF-Access-Client-Secret';

function toBase64(text: string): string {
  let bin = '';
  new TextEncoder().encode(text).forEach((b) => { bin += String.fromCharCode(b); });
  return btoa(bin);
}

/** Base64 as .NET reads it (padded, whole) and UTF-8 strictly: anything else is not a username and a password. */
function fromBase64(encoded: string): string | null {
  if (encoded.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) {
    return null;
  }
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0)));
  } catch {
    return null;
  }
}

/** A line as `ProxyAccess.Read` splits it: a name before the first colon, a value after it. */
function pair(line: string): { name: string; value: string } | null {
  const colon = line.indexOf(':');
  if (colon <= 0) {
    return null;
  }
  const value = line.slice(colon + 1).trim();
  return value.length === 0 ? null : { name: line.slice(0, colon).trim(), value };
}

/** A line the Core sends (`ServerHeaders.Parse`): a token name, never a body header. */
function sent(line: string): { name: string; value: string } | null {
  const p = pair(line);
  return p && /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(p.name) && !/^content-/i.test(p.name) ? p : null;
}

/** The method and fields a headers text holds — a method only when the text is EXACTLY what it writes (`ProxyAccess.Read`). */
export function proxyAccessOf(text: string): ProxyForm {
  const raw = text.trim();
  const form: ProxyForm = { method: 'none', user: '', password: '', token: '', clientId: '', clientSecret: '', raw };
  if (raw.length === 0) {
    return form;
  }
  const pairs = raw.split('\n').map((l) => l.trim()).filter((l) => l.length > 0).map(pair);
  if (pairs.some((p) => p === null)) {
    return { ...form, method: 'custom' };
  }
  const ps = pairs as { name: string; value: string }[];
  if (ps.length === 1 && ps[0].name.toLowerCase() === 'authorization') {
    const value = ps[0].value;
    if (/^basic /i.test(value)) {
      const decoded = fromBase64(value.slice(6).trim());
      const colon = decoded?.indexOf(':') ?? -1;
      if (decoded !== null && colon >= 0) {
        return { ...form, method: 'basic', user: decoded.slice(0, colon), password: decoded.slice(colon + 1) };
      }
    }
    if (/^bearer /i.test(value) && value.slice(7).trim().length > 0) {
      return { ...form, method: 'bearer', token: value.slice(7).trim() };
    }
  }
  const id = ps.find((p) => p.name.toLowerCase() === CF_ID.toLowerCase());
  const secret = ps.find((p) => p.name.toLowerCase() === CF_SECRET.toLowerCase());
  if (ps.length === 2 && id && secret) {
    return { ...form, method: 'cloudflare', clientId: id.value, clientSecret: secret.value };
  }
  return { ...form, method: 'custom' };
}

/** The headers text of a form: its method's fields; empty when they are (`ProxyAccess.Write`). */
export function proxyHeadersOf(form: ProxyForm): string {
  switch (form.method) {
    case 'basic':
      return form.user.trim().length > 0 || form.password.length > 0
        ? 'Authorization: Basic ' + toBase64(form.user.trim() + ':' + form.password) : '';
    case 'bearer':
      return form.token.trim().length > 0 ? 'Authorization: Bearer ' + form.token.trim() : '';
    case 'cloudflare':
      return form.clientId.trim().length > 0 && form.clientSecret.trim().length > 0
        ? `${CF_ID}: ${form.clientId.trim()}\n${CF_SECRET}: ${form.clientSecret.trim()}` : '';
    case 'custom':
      return form.raw.trim();
    default:
      return '';
  }
}

/** A header's value as the preview shows it: Cloudflare's Client ID is no secret, everything else is. */
function masked(name: string, value: string): string {
  if (name.toLowerCase() === CF_ID.toLowerCase()) {
    return value;
  }
  const space = value.indexOf(' ');
  return name.toLowerCase() === 'authorization' && space > 0 ? value.slice(0, space) + ' ••••••••' : '••••••••';
}

function hostOf(url: string): string {
  try {
    return new URL(url.trim()).hostname;
  } catch {
    return url.trim();
  }
}

/** The same address, as the Core's `ServerScan.SameAddress` reads it: this machine under any of its names is one. */
function sameAddress(a: string, b: string): boolean {
  const loopback = (u: string): string | null => {
    try {
      const url = new URL(u.trim());
      return ['localhost', '127.0.0.1', '[::1]', '::1'].includes(url.hostname.toLowerCase())
        ? `${url.protocol}//loopback:${url.port || (url.protocol === 'https:' ? '443' : '80')}` : null;
    } catch {
      return null;
    }
  };
  const la = loopback(a);
  const lb = loopback(b);
  if (la && lb) {
    return la === lb;
  }
  const norm = (u: string): string => u.trim().replace(/\/+$/, '').replace(/\/v1$/i, '').replace(/\/+$/, '').toLowerCase();
  return norm(a) === norm(b);
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

const METHODS: { code: ProxyMethod; title: string; desc: string }[] = [
  { code: 'none', title: 'ProxyMethodNone', desc: 'ProxyMethodNoneDesc' },
  { code: 'basic', title: 'ProxyMethodBasic', desc: 'ProxyMethodBasicDesc' },
  { code: 'bearer', title: 'ProxyMethodBearer', desc: 'ProxyMethodBearerDesc' },
  { code: 'cloudflare', title: 'ProxyMethodCloudflare', desc: 'ProxyMethodCloudflareDesc' },
  { code: 'custom', title: 'ProxyMethodCustom', desc: 'ProxyMethodCustomDesc' },
];

/** The docs page the "How to set this up" link opens (`DocsLinks.ReverseProxyPage`). */
export const REVERSE_PROXY_PAGE = 'remote-inference.md#behind-a-reverse-proxy';

export function proxyAccessEditor(area: HTMLTextAreaElement, host: EditorHost): StructuredEditor {
  const view = el('div', 'proxyeditor');
  let form = proxyAccessOf(area.value);
  // Another address than the saved one: what becomes of the old proxy's credentials there.
  let movedShown = false;
  let movedChoice: 'none' | 'new' | 'keep' = 'none';
  // The form's headers when another address showed the moved block: given back if the address returns.
  let beforeMove = '';
  const preview = el('div', 'proxysends');
  preview.setAttribute('role', 'group');
  const lineWarning = el('p', 'proxywarn');
  lineWarning.setAttribute('aria-live', 'polite');

  const write = (): void => {
    host.commit(area, proxyHeadersOf(form));
    drawPreview();
  };

  const drawPreview = (): void => {
    preview.textContent = '';
    const url = host.field('baseUrl');
    const lines = area.value.split('\n').map((l) => l.trim()).filter((l) => l.length > 0 && !l.startsWith('#'));
    const bad = area.value.split('\n').findIndex((l) => l.trim().length > 0 && !l.trim().startsWith('#') && sent(l.trim()) === null);
    lineWarning.textContent = bad >= 0 ? fill(host.res('ProxyLineNotSent'), String(bad + 1)) : '';
    const headers = lines.map(sent).filter((p): p is { name: string; value: string } => p !== null);
    preview.hidden = headers.length === 0;
    if (headers.length === 0) {
      return;
    }
    preview.append(el('div', 'proxysendstitle', host.res('ProxySendsTitle')));
    preview.append(el('pre', 'mono', headers.map((h) => `${h.name}: ${masked(h.name, h.value)}`).join('\n')));
    preview.append(el('p', 'hint', host.res('ProxySendsWhen')));
    preview.append(el('p', 'hint', fill(host.res('ProxySendsWhere'), hostOf(url))));
    if (/^http:\/\//i.test(url.trim())) {
      preview.append(el('p', 'proxywarn', host.res('ProxySendsHttp')));
    }
  };

  const input = (label: string, value: string, set: (v: string) => void, secret = false, hint?: string): HTMLElement => {
    const wrap = el('label', 'editfield');
    const box = el('input', secret ? undefined : 'mono');
    box.type = secret ? 'password' : 'text';
    box.value = value;
    box.spellcheck = false;
    box.addEventListener('input', () => { set(box.value); write(); });
    wrap.append(el('span', '', label), box);
    if (hint) {
      wrap.append(el('span', 'hint', hint));
    }
    return wrap;
  };

  const choose = (method: ProxyMethod): void => {
    form = { ...form, method };
    write();
    refresh();
  };

  const movedBlock = (): HTMLElement => {
    const savedUrl = host.saved('baseUrl');
    const oldHost = hostOf(savedUrl);
    const newHost = hostOf(host.field('baseUrl'));
    const block = el('div', 'proxymoved');
    block.setAttribute('role', 'group');
    block.append(el('div', 'proxymovedtitle', fill(host.res('ProxyMovedTitle'), oldHost)));
    block.append(el('p', 'hint', fill(host.res('ProxyMovedWas'), savedUrl)));
    block.append(el('p', '', fill(host.res('ProxyMovedBody'), oldHost, newHost)));
    const group = el('fieldset', 'proxychoices');
    group.append(el('legend', '', fill(host.res('ProxyMovedFor'), newHost)));
    for (const [code, key] of [['none', 'ProxyMovedNone'], ['new', 'ProxyMovedNew'], ['keep', 'ProxyMovedKeep']] as const) {
      const option = el('label', 'proxychoice' + (movedChoice === code ? ' selected' : ''));
      const radio = el('input');
      radio.type = 'radio';
      radio.name = 'proxymoved';
      radio.checked = movedChoice === code;
      radio.addEventListener('change', () => {
        movedChoice = code;
        const saved = host.saved('requestHeaders');
        area.dataset.keepHeaders = code === 'keep' ? '1' : '';
        form = proxyAccessOf(code === 'keep' ? saved : '');
        if (code === 'new') {
          const savedMethod = proxyAccessOf(saved).method;
          form = { ...form, method: savedMethod === 'none' ? 'basic' : savedMethod };
        }
        write();
        refresh();
      });
      option.append(radio, el('span', '', host.res(key)));
      group.append(option);
    }
    block.append(group);
    return block;
  };

  /** Another address than the saved one, with the old proxy's credentials saved: the default choice applies at once. */
  const followAddress = (): void => {
    const saved = host.saved('requestHeaders');
    const moved = saved.trim().length > 0 && host.field('baseUrl').trim().length > 0
      && !sameAddress(host.field('baseUrl'), host.saved('baseUrl'));
    if (moved === movedShown) {
      if (moved) {
        refresh();   // the new host's name in the block
      } else {
        drawPreview();
      }
      return;
    }
    if (moved) {
      beforeMove = area.value;
    }
    movedShown = moved;
    movedChoice = 'none';
    area.dataset.keepHeaders = '';
    const back = moved ? '' : beforeMove;
    form = proxyAccessOf(back);
    host.commit(area, back);
    refresh();
  };

  // Redraws from the form as it stands (a method just picked keeps its empty fields).
  const refresh = (): void => {
    view.textContent = '';

    // Where the proxy sits.
    const flow = el('div', 'proxyflow');
    flow.setAttribute('aria-hidden', 'true');
    flow.append(el('span', 'proxybox', 'Inferpal'), el('span', 'proxyarrow', '→'),
                el('span', 'proxybox accent', 'Proxy · Caddy · nginx · Cloudflare'), el('span', 'proxyarrow', '→'),
                el('span', 'proxybox', 'Ollama · LM Studio'));
    view.append(flow);

    if (movedShown) {
      view.append(movedBlock());
    }

    const group = el('fieldset', 'proxymethods');
    group.append(el('legend', '', host.res('ProxyQuestion')));
    for (const m of METHODS) {
      const card = el('label', 'proxycard' + (form.method === m.code ? ' selected' : ''));
      const radio = el('input');
      radio.type = 'radio';
      radio.name = 'proxymethod';
      radio.checked = form.method === m.code;
      radio.addEventListener('change', () => choose(m.code));
      const text = el('span', 'proxycardtext');
      text.append(el('b', '', host.res(m.title)), el('span', 'hint', host.res(m.desc)));
      card.append(radio, text);
      group.append(card);
    }
    view.append(group);
    const notSure = el('p', 'hint', host.res('ProxyNotSure'));
    const howTo = el('button', 'linkbtn', host.res('ProxyHowToLink'));
    howTo.type = 'button';
    howTo.addEventListener('click', () => host.post({ type: 'openDocs', page: REVERSE_PROXY_PAGE }));
    view.append(notSure, howTo);

    const fields = el('div', 'proxyfields');
    switch (form.method) {
      case 'basic':
        fields.append(el('p', '', fill(host.res('ProxyBasicIntro'), hostOf(host.field('baseUrl')))));
        fields.append(input(host.res('ProxyUser'), form.user, (v) => { form.user = v; }),
                      input(host.res('ProxyPassword'), form.password, (v) => { form.password = v; }, true));
        break;
      case 'bearer':
        fields.append(el('p', '', host.res('ProxyBearerIntro')),
                      input(host.res('ProxyMethodBearer'), form.token, (v) => { form.token = v; }, true));
        break;
      case 'cloudflare': {
        fields.append(el('p', '', host.res('ProxyCfIntro')));
        const steps = el('ol', 'proxysteps');
        steps.append(el('li', '', host.res('ProxyCfStep1')), el('li', '', host.res('ProxyCfStep2')), el('li', '', host.res('ProxyCfStep3')));
        fields.append(steps,
                      input(host.res('ProxyCfId'), form.clientId, (v) => { form.clientId = v; }, false, host.res('ProxyCfIdHint')),
                      input(host.res('ProxyCfSecretLabel'), form.clientSecret, (v) => { form.clientSecret = v; }, true));
        break;
      }
      case 'custom': {
        fields.append(el('p', '', host.res('ProxyCustomIntro')));
        const box = el('textarea', 'mono');
        box.rows = 4;
        box.spellcheck = false;
        box.value = form.raw;
        box.setAttribute('aria-label', host.res('ProxyMethodCustom'));
        box.addEventListener('input', () => { form.raw = box.value; write(); });
        fields.append(box);
        break;
      }
      default:
        break;
    }
    view.append(fields, lineWarning, preview);
    drawPreview();
  };

  host.watch('baseUrl', followAddress);
  refresh();
  return {
    view,
    refresh: () => { form = proxyAccessOf(area.value); refresh(); },
    onSaved: () => {
      movedShown = false;
      movedChoice = 'none';
      area.dataset.keepHeaders = '';
      form = proxyAccessOf(area.value);
      refresh();
    },
  };
}
