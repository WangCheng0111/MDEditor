import {all, createStarryNight} from '@wooorm/starry-night';
import {createInterface} from 'node:readline';

// Deliberately forbid runtime networking. WASM and every grammar are in the MSIX.
globalThis.fetch = async () => { throw new Error('Network is disabled in the highlighter'); };
const starry = await createStarryNight(all, {
  getOnigurumaUrlFs: () => new URL('./onig.wasm', import.meta.url),
  getOnigurumaUrlFetch: () => { throw new Error('Only local WASM is allowed'); }
});

// Index order matches MarkdownCodeTokenKind / GithubMarkdownTheme. Resolve CSS
// descendant overrides while the HAST ancestors are still available.
const classes = new Map([
  ['pl-c', 0], ['pl-c1', 1], ['pl-e', 2], ['pl-en', 2], ['pl-smi', 3],
  ['pl-s1', 3], ['pl-ent', 4], ['pl-k', 5], ['pl-s', 6], ['pl-pds', 6],
  ['pl-sr', 6], ['pl-v', 7], ['pl-smw', 7], ['pl-bu', 8], ['pl-ii', 9],
  ['pl-c2', 10], ['pl-cce', 11], ['pl-ml', 12], ['pl-mh', 13], ['pl-ms', 13],
  ['pl-md', 14], ['pl-mi1', 15], ['pl-mc', 16], ['pl-mi2', 17],
  ['pl-mdr', 18], ['pl-ba', 19], ['pl-sg', 20], ['pl-corl', 21],
  ['pl-mi', 3], ['pl-mb', 3]
]);
const cache = new Map();
let cacheBytes = 0;
const maxCacheBytes = 16 * 1024 * 1024;

function flatten(tree, text) {
  let offset = 0;
  const tokens = [];
  function visit(node, ancestors, inherited) {
    if (node.type === 'text') {
      if (text.slice(offset, offset + node.value.length) !== node.value)
        throw new Error('Highlighted text differs from input');
      if (node.value.length && inherited !== null) {
        const previous = tokens.at(-1);
        if (previous && previous.kind === inherited && previous.start + previous.length === offset)
          previous.length += node.value.length;
        else tokens.push({start: offset, length: node.value.length, kind: inherited});
      }
      offset += node.value.length;
      return;
    }
    const own = node.properties?.className || [];
    const path = [...ancestors, ...own];
    let color = inherited;
    for (const name of own) if (classes.has(name)) color = classes.get(name);
    if (path.includes('pl-mh') && own.includes('pl-en')) color = 13;
    if (path.includes('pl-s')) {
      if (own.includes('pl-v')) color = 1;
      if (own.includes('pl-s1')) color = path.includes('pl-pse') ? 6 : 3;
    }
    if (path.includes('pl-sr') && (own.includes('pl-sre') || own.includes('pl-sra'))) color = 6;
    for (const child of node.children || []) visit(child, path, color);
  }
  visit(tree, [], null);
  if (offset !== text.length) throw new Error('Incomplete highlighted text');
  return tokens;
}

function highlight(block) {
  if (!Number.isInteger(block.id) || typeof block.language !== 'string' ||
      typeof block.text !== 'string' || block.text.length > 262144 || block.language.length > 256)
    throw new Error('Invalid code block');
  const scope = starry.flagToScope(block.language);
  if (!scope) return {id: block.id, tokens: []};
  if (block.text.split(/\r\n|\r|\n/).some(line => line.length > 16384))
    return {id: block.id, tokens: [], truncated: true};
  const key = scope + '\0' + block.text;
  let result = cache.get(key);
  if (result) { cache.delete(key); cache.set(key, result); }
  else {
    const tokens = flatten(starry.highlight(block.text, scope), block.text);
    if (tokens.length > 131072) return {id: block.id, tokens: [], truncated: true};
    result = {tokens, bytes: key.length * 2 + tokens.length * 48};
    cache.set(key, result); cacheBytes += result.bytes;
    while (cache.size > 64 || cacheBytes > maxCacheBytes) {
      const oldest = cache.keys().next().value;
      cacheBytes -= cache.get(oldest).bytes; cache.delete(oldest);
    }
  }
  return {id: block.id, tokens: result.tokens, truncated: false};
}

process.stdout.write(JSON.stringify({protocol: 1, engine: 'starry-night', version: '3.11.0',
  scopes: starry.scopes().length}) + '\n');
const input = createInterface({input: process.stdin, crlfDelay: Infinity});
for await (const line of input) {
  let request;
  try {
    request = JSON.parse(line);
    if (!Number.isSafeInteger(request.id) || request.id < 1 || !Array.isArray(request.blocks) ||
        request.blocks.length > 128 ||
        request.blocks.reduce((total, block) => total + (block.text?.length || 0), 0) > 1048576)
      throw new Error('Invalid request');
    const blocks = request.blocks.map(highlight);
    process.stdout.write(JSON.stringify({id: request.id, blocks}) + '\n');
  } catch {
    process.stdout.write(JSON.stringify({id: request?.id || 0, error: 'Highlight request failed'}) + '\n');
  }
}
