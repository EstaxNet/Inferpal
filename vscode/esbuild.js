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

const allOptions = [extensionOptions, chatWebviewOptions, settingsWebviewOptions];

(async () => {
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
