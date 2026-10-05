# Offline starry-night highlighting

Code blocks use **@wooorm/starry-night 3.11.0**, its complete grammar set, TextMate and
Oniguruma WASM. The previous hand-written language scanner has been removed. The
worker returns UTF-16 color ranges; Win2D still draws native glyphs. No HTML/WebView
rendering, remote CDN, application network permission or system Node installation is used.

## Runtime and performance

- A hidden, persistent Node 22.23.3 process starts lazily when labeled code exists.
- Highlighting runs off the UI thread. Rapid edits coalesce for 30 ms **for colors only**;
  text, caret, input methods and paragraph reflow do not wait for the highlighter.
- Identical language/code blocks use bounded in-memory caches. Nothing is logged to disk.
- Color replies are checked, mapped through source/projection coordinates, and discarded
  when obsolete. A color refresh cannot change line breaks or text selection.
- Unknown languages remain plain. Excessive blocks (128), total code (1 Mi UTF-16),
  blocks over 256 Ki UTF-16, and single lines over 16 Ki UTF-16 have bounded coloring;
  the complete source remains editable and is never truncated.
- Timeout/crash/missing-runtime errors fall back to uncolored native code, not an empty
  document. Closing the editor terminates its highlighter process.

## Building / publishing

The checked-in `MDEditor/Assets/StarryNight` payload is deliberate application content,
not temporary output. It includes the bundled JS, matched WASM, grammar notices, license
texts and official checksum-verified Node binaries. MSIX copies **only the selected RID's**
runtime (`win-x64`, `win-x86`, or `win-arm64`). Normal single-project publishing is enough;
no Windows Application Packaging Project or separate user installer is required.

To regenerate after intentionally updating dependencies, in `tools/StarryNight` run:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm run build
.\Download-Node.ps1
npm test
```

Only this development regeneration downloads files. Ordinary app build, publish and app
use are offline. The app build fails early if required payload files are missing. Runtime
binaries must stay in the repository/distribution; `node_modules` need not be retained.

The native palette follows starry-night's light/dark CSS. Font weights, geometry and the
existing editor code font are unchanged; non-HTML diff/error colors use readable native
foregrounds rather than introducing another layout model.
