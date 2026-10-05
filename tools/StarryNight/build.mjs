import {build} from 'esbuild';
import {copyFile, mkdir, readFile, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {dirname, resolve} from 'node:path';

const require = createRequire(import.meta.url);
const output = resolve(import.meta.dirname, '../../MDEditor/Assets/StarryNight');
await mkdir(output, {recursive: true});
await build({entryPoints: [resolve(import.meta.dirname, 'worker.mjs')],
  outfile: resolve(output, 'worker.mjs'), bundle: true, platform: 'node', format: 'esm',
  target: 'node22', minify: true, legalComments: 'eof',
  banner: {js: "import {createRequire as mdCreateRequire} from 'node:module'; const require = mdCreateRequire(import.meta.url);"}});
const onig = dirname(require.resolve('vscode-oniguruma/package.json'));
await copyFile(resolve(onig, 'release/onig.wasm'), resolve(output, 'onig.wasm'));
const starry = dirname(require.resolve('@wooorm/starry-night'));
await copyFile(resolve(starry, 'notice'), resolve(output, 'GRAMMAR-NOTICES.txt'));
let licenses = 'MDEditor offline syntax highlighting: @wooorm/starry-night 3.11.0\n\n';
for (const [name, file] of [['@wooorm/starry-night', resolve(starry, 'license')],
  ['vscode-textmate', resolve(dirname(require.resolve('vscode-textmate/package.json')), 'LICENSE.md')],
  ['vscode-oniguruma', resolve(onig, 'LICENSE.txt')],
  ['Oniguruma third-party notices', resolve(onig, 'NOTICES.txt')],
  ['import-meta-resolve', resolve(dirname(require.resolve('import-meta-resolve/package.json')), 'license')]])
  licenses += `\n--- ${name} ---\n` + await readFile(file, 'utf8');
await writeFile(resolve(output, 'THIRD-PARTY-LICENSES.txt'), licenses);
for (const mode of ['light', 'dark'])
  await copyFile(resolve(starry, `style/${mode}.css`), resolve(output, `${mode}.css`));
