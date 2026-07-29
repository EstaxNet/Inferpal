// Inferpal chat webview. The extension host owns the transcript; this script only
// renders state pushed via postMessage and reports user intents back. It must
// survive being destroyed on hide: everything re-renders from the 'hydrate' message.
(function () {
  'use strict';

  const vscode = acquireVsCodeApi();

  const messagesEl = document.getElementById('messages');
  const promptEl = document.getElementById('prompt');
  const modelEl = document.getElementById('model');
  const sendBtn = document.getElementById('send');
  const cancelBtn = document.getElementById('cancel');
  const resetBtn = document.getElementById('reset');
  const statusEl = document.getElementById('statusline');

  let busy = false;
  let streamEl = null; // live assistant bubble while tokens stream

  // ── Minimal safe markdown: escape everything, then re-introduce code blocks. ──
  function escapeHtml(text) {
    return text
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  function renderMarkdown(text) {
    const escaped = escapeHtml(text);
    // fenced code blocks first, then inline code and bold on the remainder
    let html = escaped.replace(/```([^\n`]*)\n([\s\S]*?)```/g, (_m, _lang, code) =>
      '<pre><code>' + code + '</code></pre>');
    html = html.replace(/`([^`\n]+)`/g, '<code>$1</code>');
    html = html.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
    return html.replace(/\n/g, '<br>');
  }

  function addBubble(role, html) {
    const el = document.createElement('div');
    el.className = 'bubble ' + role;
    el.innerHTML = html;
    messagesEl.appendChild(el);
    messagesEl.scrollTop = messagesEl.scrollHeight;
    return el;
  }

  function setBusy(value) {
    busy = value;
    sendBtn.hidden = value;
    cancelBtn.hidden = !value;
    if (!value) {
      statusEl.hidden = true;
      statusEl.textContent = '';
    }
  }

  function setStatus(text) {
    statusEl.textContent = text;
    statusEl.hidden = !text;
  }

  function ensureStreamBubble() {
    if (!streamEl) {
      streamEl = addBubble('assistant streaming', '');
      streamEl.dataset.raw = '';
    }
    return streamEl;
  }

  function finishStream() {
    if (streamEl) {
      streamEl.classList.remove('streaming');
      streamEl = null;
    }
  }

  // Renders an approval message with unified-diff coloring: lines starting with
  // +/- get add/del classes (the host embeds the real diff for file writes).
  function renderApprovalMessage(message) {
    const pre = document.createElement('pre');
    pre.className = 'approval-text';
    for (const line of message.split('\n')) {
      const span = document.createElement('span');
      span.textContent = line + '\n';
      if (/^\+(?!\+\+)/.test(line)) {
        span.className = 'diff-add';
      } else if (/^-(?!--)/.test(line)) {
        span.className = 'diff-del';
      }
      pre.appendChild(span);
    }
    return pre;
  }

  // Approval card: three-way answer (0 deny / 1 once / 2 always), buttons freeze
  // after the click so the outcome stays visible in the transcript.
  function addApprovalCard(id, message) {
    finishStream();
    const card = document.createElement('div');
    card.className = 'bubble approval';
    const text = document.createElement('div');
    text.className = 'approval-message';
    text.appendChild(renderApprovalMessage(message));
    const actions = document.createElement('div');
    actions.className = 'approval-actions';
    const buttons = [
      { label: 'Deny', answer: 0, cls: 'deny' },
      { label: 'Allow once', answer: 1, cls: 'allow' },
      { label: 'Always (session)', answer: 2, cls: 'allow' },
    ];
    for (const spec of buttons) {
      const btn = document.createElement('button');
      btn.textContent = spec.label;
      btn.className = spec.cls;
      btn.addEventListener('click', () => {
        vscode.postMessage({ type: 'approvalAnswer', id, answer: spec.answer });
        card.classList.add('answered');
        for (const b of actions.querySelectorAll('button')) {
          b.disabled = true;
        }
        btn.classList.add('chosen');
      });
      actions.appendChild(btn);
    }
    // Open the raw message as a diff-language document (real editor, searchable).
    const openBtn = document.createElement('button');
    openBtn.textContent = '⧉';
    openBtn.title = 'Open in editor';
    openBtn.addEventListener('click', () => vscode.postMessage({ type: 'openApprovalDiff', text: message }));
    actions.appendChild(openBtn);
    card.appendChild(text);
    card.appendChild(actions);
    messagesEl.appendChild(card);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  // ── @-mention popup ─────────────────────────────────────────────────────────
  const mentionsEl = document.createElement('div');
  mentionsEl.id = 'mentions';
  mentionsEl.hidden = true;
  document.getElementById('composer').prepend(mentionsEl);

  let mentionStart = -1; // offset of '@' in the textarea, -1 = popup closed
  let mentionIndex = 0;

  function closeMentions() {
    mentionsEl.hidden = true;
    mentionsEl.textContent = '';
    mentionStart = -1;
    mentionIndex = 0;
  }

  function detectMention() {
    const caret = promptEl.selectionStart;
    const before = promptEl.value.slice(0, caret);
    const match = before.match(/@([^\s@]*)$/);
    if (!match) {
      closeMentions();
      return;
    }
    mentionStart = caret - match[0].length;
    vscode.postMessage({ type: 'mentionQuery', query: match[1] });
  }

  function insertMention(path) {
    const caret = promptEl.selectionStart;
    const value = promptEl.value;
    promptEl.value = value.slice(0, mentionStart) + '@' + path + ' ' + value.slice(caret);
    const pos = mentionStart + path.length + 2;
    promptEl.setSelectionRange(pos, pos);
    promptEl.focus();
    closeMentions();
  }

  function renderMentions(items) {
    if (mentionStart < 0 || items.length === 0) {
      closeMentions();
      return;
    }
    mentionsEl.textContent = '';
    mentionIndex = Math.min(mentionIndex, items.length - 1);
    items.forEach((path, i) => {
      const row = document.createElement('div');
      row.className = 'mention-item' + (i === mentionIndex ? ' selected' : '');
      row.textContent = path;
      row.addEventListener('mousedown', (e) => {
        e.preventDefault(); // keep textarea focus
        insertMention(path);
      });
      mentionsEl.appendChild(row);
    });
    mentionsEl.hidden = false;
  }

  promptEl.addEventListener('input', detectMention);
  promptEl.addEventListener('blur', () => setTimeout(closeMentions, 150));

  // ── Context X-Ray panel (interactive /xray V2) ─────────────────────────────
  // Ephemeral overlay above the transcript; state lives host-side, this only renders
  // the pushed panel model. Expansion state survives toggle-driven re-renders.
  const xrayEl = document.createElement('div');
  xrayEl.id = 'xray';
  xrayEl.hidden = true;
  document.body.appendChild(xrayEl);
  const xrayExpanded = new Set();

  function renderXray(panel) {
    xrayEl.textContent = '';

    const header = document.createElement('div');
    header.className = 'xray-header';
    const title = document.createElement('span');
    title.className = 'xray-title';
    title.textContent = '🩻 Context X-Ray — ~' + panel.totalTokens.toLocaleString() + ' tokens';
    const copyBtn = document.createElement('button');
    copyBtn.textContent = 'Copy prompt';
    copyBtn.title = 'Copy the exact system prompt of the next turn';
    copyBtn.addEventListener('click', () => vscode.postMessage({ type: 'xrayCopy', text: panel.rawPrompt }));
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
      check.title = s.canToggle ? 'Include in the next turn' : '';
      check.addEventListener('change', () =>
        vscode.postMessage({ type: 'xrayToggle', id: s.id, enabled: check.checked }));
      const label = document.createElement('span');
      label.className = 'xray-label';
      label.textContent = s.label;
      const tokens = document.createElement('span');
      tokens.className = 'xray-tokens';
      tokens.textContent = '~' + s.tokens.toLocaleString();
      const chevron = document.createElement('button');
      chevron.className = 'xray-chevron';
      chevron.textContent = xrayExpanded.has(s.id) ? '▾' : '▸';
      const toggleContent = () => {
        if (xrayExpanded.has(s.id)) { xrayExpanded.delete(s.id); } else { xrayExpanded.add(s.id); }
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

      if (xrayExpanded.has(s.id)) {
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
      warn.textContent = '⚠ Project layers (rules, memory, notes) take a large share of the context — consider trimming them.';
      footer.appendChild(warn);
    }
    const info = document.createElement('div');
    info.textContent = 'History: ~' + panel.historyTokens.toLocaleString() + ' tokens'
      + (panel.contextWindow > 0 ? ' · window ' + panel.fillPercent.toFixed(0) + '% full' : '');
    footer.appendChild(info);
    const hint = document.createElement('div');
    hint.textContent = 'Unchecked sections are excluded from the next turn.';
    footer.appendChild(hint);
    xrayEl.appendChild(footer);

    xrayEl.hidden = false;
  }

  // ── Intents → extension ────────────────────────────────────────────────────
  function send() {
    const text = promptEl.value;
    if (!text.trim() || busy) {
      return;
    }
    closeMentions();
    promptEl.value = '';
    vscode.postMessage({ type: 'send', text });
  }

  sendBtn.addEventListener('click', send);
  cancelBtn.addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
  resetBtn.addEventListener('click', () => vscode.postMessage({ type: 'reset' }));
  modelEl.addEventListener('change', () => vscode.postMessage({ type: 'pickModel', model: modelEl.value }));
  promptEl.addEventListener('keydown', (e) => {
    if (!mentionsEl.hidden) {
      const rows = mentionsEl.querySelectorAll('.mention-item');
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        mentionIndex = (mentionIndex + (e.key === 'ArrowDown' ? 1 : rows.length - 1)) % rows.length;
        rows.forEach((r, i) => r.classList.toggle('selected', i === mentionIndex));
        return;
      }
      if (e.key === 'Tab' || e.key === 'Enter') {
        e.preventDefault();
        insertMention(rows[mentionIndex].textContent);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        closeMentions();
        return;
      }
    }
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  });

  // ── State ← extension ──────────────────────────────────────────────────────
  window.addEventListener('message', (event) => {
    const msg = event.data;
    switch (msg.type) {
      case 'hydrate': {
        messagesEl.textContent = '';
        streamEl = null;
        for (const item of msg.transcript) {
          if (item.role === 'tool') {
            addBubble('tool', '🛠 <code>' + escapeHtml(item.text) + '</code>');
          } else if (item.role === 'error') {
            addBubble('error', renderMarkdown(item.text));
          } else if (item.role === 'user' || item.role === 'assistant') {
            addBubble(item.role, renderMarkdown(item.text));
          }
        }
        modelEl.textContent = '';
        for (const name of msg.models) {
          const opt = document.createElement('option');
          opt.value = name;
          opt.textContent = name;
          opt.selected = name === msg.model;
          modelEl.appendChild(opt);
        }
        if (msg.model && !msg.models.includes(msg.model)) {
          const opt = document.createElement('option');
          opt.value = msg.model;
          opt.textContent = msg.model;
          opt.selected = true;
          modelEl.appendChild(opt);
        }
        setBusy(msg.busy);
        if (msg.busy && msg.stream) {
          const el = ensureStreamBubble();
          el.dataset.raw = msg.stream;
          el.innerHTML = renderMarkdown(msg.stream);
        }
        break;
      }
      case 'turnStarted':
        addBubble('user', renderMarkdown(msg.prompt));
        setBusy(true);
        break;
      case 'token': {
        const el = ensureStreamBubble();
        el.dataset.raw += msg.text;
        el.innerHTML = renderMarkdown(el.dataset.raw);
        messagesEl.scrollTop = messagesEl.scrollHeight;
        break;
      }
      case 'thinking':
        setStatus('Thinking…');
        break;
      case 'status':
        setStatus(msg.text);
        break;
      case 'tool':
        finishStream();
        addBubble('tool', '🛠 <code>' + escapeHtml(msg.name) + '</code>');
        break;
      case 'approval':
        addApprovalCard(msg.id, msg.message);
        break;
      case 'mentionSuggestions':
        renderMentions(msg.items);
        break;
      case 'xrayPanel':
        renderXray(msg.panel);
        break;
      case 'streamReset':
        if (streamEl) {
          streamEl.remove();
          streamEl = null;
        }
        break;
      case 'turnEnded': {
        if (streamEl) {
          // Replace the stream bubble content with the authoritative final text.
          streamEl.dataset.raw = msg.text || streamEl.dataset.raw;
          streamEl.innerHTML = renderMarkdown(streamEl.dataset.raw);
          finishStream();
        } else if (msg.text) {
          addBubble('assistant', renderMarkdown(msg.text));
        }
        if (msg.error) {
          addBubble('error', renderMarkdown(msg.error));
        }
        if (msg.cancelled) {
          addBubble('error', 'Cancelled.');
        }
        setBusy(false);
        messagesEl.scrollTop = messagesEl.scrollHeight;
        break;
      }
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
