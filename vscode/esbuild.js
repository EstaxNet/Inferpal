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

/** @type {import('esbuild').BuildOptions} */
const webviewOptions = {
  entryPoints: ['src/webview/main.ts'],
  bundle: true,
  outfile: 'media/chat.js',
  platform: 'browser',
  target: 'es2022',
  format: 'iife',
  sourcemap: true,
  minify: !watch,
};

(async () => {
  if (watch) {
    const contexts = await Promise.all([esbuild.context(extensionOptions), esbuild.context(webviewOptions)]);
    await Promise.all(contexts.map((ctx) => ctx.watch()));
    console.log('[esbuild] watching…');
  } else {
    await Promise.all([esbuild.build(extensionOptions), esbuild.build(webviewOptions)]);
    console.log('[esbuild] build done');
  }
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
