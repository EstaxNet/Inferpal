// Webview-host API injected by VS Code into every webview (no import — global).
declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

// Localized strings injected by chatViewProvider.renderHtml (<script> before the bundle).
interface Window {
  __l10n?: Record<string, string>;
}
