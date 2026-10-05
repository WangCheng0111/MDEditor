import {test, after} from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {resolve} from 'node:path';

const payload = resolve(import.meta.dirname, '../../MDEditor/Assets/StarryNight');
const child = spawn(resolve(payload, 'runtimes/win-x64/node.exe'), [resolve(payload, 'worker.mjs')],
  {windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {...process.env, NODE_OPTIONS: '', NODE_PATH: ''}});
const lines = createInterface({input: child.stdout})[Symbol.asyncIterator]();
const ready = JSON.parse((await lines.next()).value);
assert.equal(ready.engine, 'starry-night');
assert.equal(ready.version, '3.11.0');
assert.ok(ready.scopes >= 600);
let sequence = 0;
after(() => { child.stdin.end(); child.kill(); });
async function highlight(language, text) {
  const id = ++sequence;
  child.stdin.write(JSON.stringify({id, blocks: [{id: 0, language, text}]}) + '\n');
  const result = JSON.parse((await lines.next()).value);
  assert.equal(result.id, id); assert.equal(result.error, undefined);
  let end = 0;
  for (const token of result.blocks[0].tokens) {
    assert.ok(token.start >= end); assert.ok(token.length > 0);
    assert.ok(token.start + token.length <= text.length);
    assert.ok(token.kind >= 0 && token.kind < 22); end = token.start + token.length;
  }
  return result.blocks[0];
}
for (const [language, text] of [
  ['cs', '// 中文😀\r\nint answer = 42; Console.WriteLine("hi");\r\n'],
  ['python', 's = """line1\n\n中文line3"""\nfor i in range(3):\n    print(i)\n'],
  ['html', '<script>const x = 1;</script><style>body{color:red}</style>'],
  ['tsx', 'const App = () => <div title="hello">中文</div>;'],
  ['sql', 'SELECT id FROM users WHERE count > 42;'],
  ['rust', 'fn main() { println!("hi"); }'],
  ['json', '{"中文": 42, "ok": true}'],
  ['diff', '+ added\n- removed\n'],
  ['yaml', 'name: hello\ncount: 42\n'],
  ['bash', '# note\nfor f in *.txt; do echo "$f"; done']
]) test(language, async () => assert.ok((await highlight(language, text)).tokens.length > 0));
test('unknown language remains plain', async () => assert.deepEqual((await highlight('unknown-123', 'for = 42')).tokens, []));
test('empty code', async () => assert.deepEqual((await highlight('cs', '')).tokens, []));
test('cached reply matches original', async () => {
  const first = await highlight('js', 'const x = "中文😀";');
  assert.deepEqual(await highlight('js', 'const x = "中文😀";'), first);
});
test('long line is bounded', async () => assert.equal((await highlight('js', 'x'.repeat(16385))).truncated, true));
test('malformed request does not kill the worker', async () => {
  child.stdin.write('{invalid json}\n');
  assert.ok(JSON.parse((await lines.next()).value).error);
  assert.ok((await highlight('js', 'const x = 1;')).tokens.length > 0);
});
