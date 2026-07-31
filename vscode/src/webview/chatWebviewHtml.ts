// Chat webview shell: the HTML document and the localized string table injected into it.
// Extracted from ChatViewProvider — neither needs any of the provider's state, and together they
// were 150 lines of the class.
import * as crypto from 'crypto';
import * as vscode from 'vscode';

/** Localized strings injected into the chat webview as `window.__l10n`. */
export function webviewStrings(): Record<string, string> {
  const t = vscode.l10n.t;
  return {
    promptPlaceholder: t('Ask Inferpal…'),
    modelTitle: t('Model'),
    sendTitle: t('Send'),
    cancelTitle: t('Cancel'),
    retry: t('Retry the connection'),
    searchTitle: t('Search in the conversation'),
    searchPlaceholder: t('Search in the conversation…'),
    statusConnected: t('Connected'),
    statusUnreachable: t('Backend unreachable'),
    modeAgent: t('Agent'),
    modeChat: t('Chat'),
    modeToggleTitle: t('Toggle between agent mode (tools) and plain chat'),
    hintBar: t('Enter to send · Shift+Enter for a new line · Shift+↑↓ history'),
    tokensInfo: t('{0} tokens'),
    contextTooltip: t('Context: {0} / {1} tokens ({2}%) — click for the X-Ray panel'),
    thinking: t('Thinking…'),
    cancelled: t('Cancelled.'),
    copy: t('Copy'),
    copied: t('Copied!'),
    regenerate: t('Regenerate'),
    deny: t('Deny'),
    allowOnce: t('Allow once'),
    allowAlways: t('Always (session)'),
    openInEditor: t('Open in editor'),
    toolError: t('error'),
    welcomeSubtitle: t('Ask a question about your code'),
    cardExplain: t('Explain the selection'),
    cardFix: t('Fix an error'),
    cardTest: t('Generate a test'),
    cardHelp: t('See all commands'),
    xrayTitle: t('🩻 Context X-Ray — ~{0} tokens'),
    xrayCopyPrompt: t('Copy prompt'),
    xrayCopyPromptTitle: t('Copy the exact system prompt of the next turn'),
    xrayInclude: t('Include in the next turn'),
    xrayWarning: t('⚠ Project layers (rules, memory, notes) take a large share of the context — consider trimming them.'),
    xrayHistory: t('History: ~{0} tokens'),
    xrayWindow: t('window {0}% full'),
    xrayHint: t('Unchecked sections are excluded from the next turn.'),
    mentionSearchCode: t('Search the codebase for "{0}"'),
    stepPaused: t('⏸ Agent paused after tool call. Resume to continue, or Cancel to abort.'),
    resume: t('Resume'),
    fixWithAi: t('Fix with AI'),
    fixPrompt: t('Fix the following errors:'),
    chipRemove: t('Remove'),
    attachMenuTitle: t('Add context'),
    attachActiveFile: t('Attach the active file'),
    attachSelection: t('Attach the selection'),
    attachBrowse: t('Attach a file from disk'),
  };
}

/**
 * The chat webview document. Static shell: the whole UI is built by media/chat.js — the only
 * dynamic parts are the nonce, the asset URIs and the injected string table.
 */
export function renderChatHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
  const script = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'chat.js'));
  const style = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'chat.css'));
  const nonce = crypto.randomBytes(16).toString('base64');
  // </script> inside a translation would close the tag early — escape all '<'.
  const l10n = JSON.stringify(webviewStrings()).replace(/</g, '\\u003c');
  // CSP: no inline scripts except the nonce'd l10n bootstrap; assets restricted to media/.
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
    content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource};">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>Inferpal</title>
</head>
<body>
<div id="topbar"></div>
<div id="messages"></div>
<div id="composer">
<div id="plan" hidden></div>
<div id="statusline" hidden></div>
<textarea id="prompt" rows="3"></textarea>
<div id="toolbar"></div>
<div id="footerbar"></div>
</div>
<script nonce="${nonce}">
window.__l10n = ${l10n};
// Webview crashes are invisible (no console in logs): channel them to the extension,
// which writes them to the Inferpal output channel.
window.__vsapi = acquireVsCodeApi();
window.onerror = function (message, source, line, col) {
try { window.__vsapi.postMessage({ type: 'clientError', message: String(message) + ' @ ' + source + ':' + line + ':' + col }); } catch (e) { }
};
// Capture phase: resource-load failures (script/css) never reach window.onerror.
window.addEventListener('error', function (e) {
try {
  var target = e.target;
  if (target && (target.src || target.href)) {
    window.__vsapi.postMessage({ type: 'clientError', message: 'resource failed: ' + (target.src || target.href) });
  }
} catch (err) { }
}, true);
window.addEventListener('unhandledrejection', function (e) {
try { window.__vsapi.postMessage({ type: 'clientError', message: 'unhandled rejection: ' + String(e.reason) }); } catch (err) { }
});
</script>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
}
