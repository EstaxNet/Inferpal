// Bundles the extension for the VS Code extension host (Node.js, CJS) and the chat
// webview (browser IIFE — markdown-it is bundled so the CSP stays nonce-only, no CDN).
// 'vscode' is provided by the host at runtime and must stay external (extension only).
const esbuild = require('esbuild');

const watch = process.argv.includes('--watch');

/** @type {import('esbuild').BuildOptions} */
const extensionOptions = {
  entryPoints: ['src/extension.ts'],
  bundle: true,
  outfile: 'out/extension.js',
  external: ['vscode'],
  platform: 'node',
  target: 'node20',
  format: 'cjs',
  sourcemap: true,
  minify: !watch,
};

/** Shared options of the webview bundles (browser IIFE). */
const webviewBase = {
  bundle: true,
  platform: 'browser',
  target: 'es2022',
  format: 'iife',
  sourcemap: true,
  minify: !watch,
};

/** @type {import('esbuild').BuildOptions} */
const chatWebviewOptions = { ...webviewBase, entryPoints: ['src/webview/main.ts'], outfile: 'media/chat.js' };

/** @type {import('esbuild').BuildOptions} */
const settingsWebviewOptions = { ...webviewBase, entryPoints: ['src/webview/settings.ts'], outfile: 'media/settings.js' };

/** The settings form in a browser page (`Inferpal.Host --settings`), with the shell that plays the extension's part. */
/** @type {import('esbuild').BuildOptions} */
const standaloneSettingsOptions = {
  ...webviewBase, entryPoints: ['src/webview/settingsStandalone.ts'], outfile: 'media/settings-standalone.js',
};

const allOptions = [extensionOptions, chatWebviewOptions, settingsWebviewOptions, standaloneSettingsOptions];

(async () => {
  // Mermaid (third-party/mermaid, MIT), drawn by the chat page: copied beside it with its licence, watched builds too.
  const fs = require('fs');
  const path = require('path');
  const from = path.join(__dirname, '..', 'third-party', 'mermaid');
  const to = path.join(__dirname, 'media', 'mermaid');
  fs.mkdirSync(to, { recursive: true });
  for (const f of ['mermaid.min.js', 'LICENSE']) fs.copyFileSync(path.join(from, f), path.join(to, f));
  // The licence of the OCR recognizers the host embeds (third-party/paddleocr-models, Apache-2.0), shipped beside it.
  const ocr = path.join(__dirname, 'media', 'paddleocr-models');
  fs.mkdirSync(ocr, { recursive: true });
  for (const f of ['LICENSE', 'README.md'])
    fs.copyFileSync(path.join(__dirname, '..', 'third-party', 'paddleocr-models', f), path.join(ocr, f));
  if (watch) {
    const contexts = await Promise.all(allOptions.map((o) => esbuild.context(o)));
    await Promise.all(contexts.map((ctx) => ctx.watch()));
    console.log('[esbuild] watching…');
  } else {
    await Promise.all(allOptions.map((o) => esbuild.build(o)));
    console.log('[esbuild] build done');
  }
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
