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
  /** The settings page, and the section of it, asked for when the panel opened (a cut turn's "Open time limits"). */
  __settingsPage?: { page: string; section?: string } | null;
  __vsapi?: {
    postMessage(message: unknown): void;
    getState(): unknown;
    setState(state: unknown): void;
  };
}
