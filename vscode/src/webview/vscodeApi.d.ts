// Webview-host API injected by VS Code into every webview (no import — global).
declare function acquireVsCodeApi(): {
  postMessage(message: unknown): void;
  getState(): unknown;
  setState(state: unknown): void;
};

// Injected by the renderHtml bootstrap script (before the bundle): localized strings
// and the single acquired VS Code API (acquireVsCodeApi can only be called once).
interface Window {
  __l10n?: Record<string, string>;
  __vsapi?: {
    postMessage(message: unknown): void;
    getState(): unknown;
    setState(state: unknown): void;
  };
}
