// Bundles the extension for the VS Code extension host (Node.js, CJS).
// 'vscode' is provided by the host at runtime and must stay external.
const esbuild = require('esbuild');

const watch = process.argv.includes('--watch');

/** @type {import('esbuild').BuildOptions} */
const options = {
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

(async () => {
  if (watch) {
    const ctx = await esbuild.context(options);
    await ctx.watch();
    console.log('[esbuild] watching…');
  } else {
    await esbuild.build(options);
    console.log('[esbuild] build done');
  }
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
