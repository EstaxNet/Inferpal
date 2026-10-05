// Chat webview shell: the HTML document and the localized string table injected into it.
// Extracted from ChatViewProvider — neither needs any of the provider's state, and together they
// were 150 lines of the class.
import * as crypto from 'crypto';
import * as vscode from 'vscode';
import { t } from '../i18n';

/** Localized strings injected into the chat webview as `window.__l10n`. */
export function webviewStrings(): Record<string, string> {
  return {
    sendTitle: t('Send'),
    cancelTitle: t('Cancel'),
    retry: t('Retry the connection'),
    searchPlaceholder: t('Search in the conversation…'),
    statusUnreachable: t('Backend unreachable'),
    noModelListed: t('No model listed — is the backend reachable?'),
    modeAgent: t('Agent'),
    modeChat: t('Chat'),
    tokensInfo: t('{0} tokens'),
    thinking: t('Thinking…'),
    cancelled: t('Cancelled.'),
    copy: t('Copy'),
    copied: t('Copied!'),
    regenerate: t('Regenerate'),
    deny: t('Deny'),
    allowOnce: t('Allow once'),
    allowAlways: t('Always this session'),
    cardProject: t('Explain this project'),
    cardChanges: t('Review my changes'),
    welcomeProjectPrompt: t('Give me an overview of this project: what it does, how it is organized, and where to start reading the code.'),
    welcomeChangesPrompt: t('Review my uncommitted changes: point out bugs, risks and anything missing, such as tests.'),
    welcomeOpenFileHint: t('Open a file to explain it, write its tests or find its usages.'),
    cardHelp: t('See all commands'),
    close: t('Close'),
    xrayTitle: t('Context X-Ray — ~{0} tokens'),
    xrayCopyPrompt: t('Copy prompt'),
    xrayCopyPromptTitle: t('Copy the exact system prompt of the next turn'),
    xrayInclude: t('Include in the next turn'),
    xrayWarning: t('Project layers (rules, memory, notes) take a large share of the context — consider trimming them.'),
    xrayHistory: t('History: ~{0} tokens'),
    xrayTools: t('Tool definitions: ~{0} tokens'),
    xrayWindow: t('window {0}% full'),
    xrayHint: t('Unchecked sections are excluded from the next turn.'),
    mentionSearchCode: t('Search the codebase for "{0}"'),
    stepPaused: t('Agent paused after tool call. Resume to continue, or Cancel to abort.'),
    resume: t('Resume'),
    fixWithAi: t('Fix with AI'),
    fixPrompt: t('Fix the following errors:'),
    chipRemove: t('Remove'),
    attachActiveFile: t('Attach the active file'),
    attachSelection: t('Attach the selection'),
    attachBrowse: t('Attach a file from disk'),
    pinActiveFile: t('Pin the active file'),
    unpin: t('Unpin'),
    // The redesigned panel: header, mode switch, context ring, turns, approval card, welcome.
    chatNewConversation: t('New conversation'),
    chatConversations: t('Conversations'),
    chatMore: t('More'),
    chatMenuSearch: t('Search in the conversation'),
    chatMenuExport: t('Export the conversation'),
    chatMenuXray: t('Context X-Ray'),
    chatMenuSettings: t('Settings'),
    chatModelButton: t('Model and server: {0}'),
    chatAttach: t('Attach a file, a selection or problems'),
    modePlan: t('Plan'),
    modeChatTip: t('Answers without tools'),
    modeAgentTip: t('Uses tools; asks before changing anything'),
    modePlanTip: t('Reads and plans; changes nothing'),
    contextRingTip: t('Context: {0}% of the {1}-token window — click for the X-Ray'),
    composerPlaceholder: t('Ask, or type @ to attach and / for commands'),
    turnWorking: t('working · step {0}'),
    turnWaiting: t('waiting for you'),
    runUndo: t('Undo run'),
    approvalOpenDiff: t('Open diff'),
    stepsOne: t('1 step'),
    stepsMany: t('{0} steps'),
    welcomeTitle: t('What should we work on?'),
    welcomeLine: t('{0} on {1}. Your code stays on this machine.'),
    welcomeLocal: t('Your code stays on this machine.'),
    welcomeForFile: t('For {0}, open in the editor'),
    welcomeExplainFile: t('Explain this file'),
    welcomeExplainFileDesc: t('What it does and how the pieces connect'),
    welcomeTestsFile: t('Write tests for it'),
    welcomeTestsFileDesc: t('Placed where your test runner finds them'),
    welcomeUsagesFile: t('Find where it is used'),
    welcomeUsagesFileDesc: t('Callers and dependants across the workspace'),
    welcomeUsagesPrompt: t('Where is {0} used? List its callers and what depends on it.'),
    welcomeProblems: t('{0} error(s) in the Problems panel'),
    welcomeFixThem: t('Fix them'),
    welcomeFixPrompt: t('Fix these errors.'),
    welcomeHintAttach: t('attach a file'),
    welcomeHintCommands: t('commands'),
    welcomeHintNewLine: t('new line'),
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
<textarea id="prompt" rows="3"></textarea>
<div id="toolbar"></div>
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
