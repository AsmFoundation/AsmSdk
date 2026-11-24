// bench-chrome.mjs - runs Crc32C.Browser's "bench" (or "verify") in headless Chrome, through
// bench.html in a copy of the app bundle, and prints what it printed, after Chrome's version.
// With --pin, the page starts the .NET runtime and waits; pin-chrome.ps1 then pins every process
// of this Chrome to CPU 2 (High priority, power throttling off), and only then does the benchmark
// start. Python's http.server serves the bundle on 127.0.0.1 (it maps .wasm and .mjs to the types
// browsers require); Chrome runs with a throw-away profile, deleted afterwards.
//   node bench-chrome.mjs <bundle copy> [mode] [--pin]
// Needs Chrome at its default Windows path, Python 3 and Node 22 (WebSocket and fetch built in).
import { spawn, execFileSync } from 'node:child_process';
import { rmSync, mkdtempSync, copyFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [bundle, mode = 'bench'] = process.argv.slice(2).filter((a) => !a.startsWith('--'));
const pin = process.argv.includes('--pin');
const httpPort = 8767, cdpPort = 9335;
copyFileSync(join(here, 'bench.html'), join(bundle, 'bench.html'));
const profile = mkdtempSync(join(tmpdir(), 'crc32c-chrome-'));
const server = spawn('python', ['-I', '-m', 'http.server', String(httpPort), '--bind', '127.0.0.1', '--directory', bundle], { stdio: 'ignore' });
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new',
  `--remote-debugging-port=${cdpPort}`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  `http://127.0.0.1:${httpPort}/bench.html?mode=${mode}${pin ? '&wait=1' : ''}`], { stdio: 'ignore' });

/**
 * Opens a DevTools protocol session on the page bench.html runs in, waiting up to 15 s for Chrome.
 * @returns {Promise<{ws: WebSocket, evaluate: (expression: string) => Promise<any>}>} The socket
 *   and a function that evaluates an expression in the page and returns its value.
 * @throws {Error} When the page does not appear in time.
 */
async function attach() {
  for (let i = 0; i < 150; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json();
      const page = targets.find((t) => t.type === 'page' && t.url.includes('bench.html'));
      if (page) {
        const ws = new WebSocket(page.webSocketDebuggerUrl);
        await new Promise((resolve) => ws.addEventListener('open', resolve, { once: true }));
        let id = 1;
        const pending = new Map();
        ws.addEventListener('message', (event) => {
          const message = JSON.parse(event.data);
          if (message.id) pending.get(message.id)?.(message.result);
        });
        /**
         * Evaluates an expression in the page (Runtime.evaluate) and resolves with its value.
         * @param {string} expression JavaScript to run in the page.
         * @returns {Promise<any>} The value, returned by value (undefined when it threw).
         */
        const evaluate = (expression) => new Promise((resolve) => {
          const n = id++;
          pending.set(n, (result) => resolve(result?.result?.value));
          ws.send(JSON.stringify({ id: n, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }));
        });
        return { ws, evaluate };
      }
    } catch { /* Chrome is not listening yet */ }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('the page never appeared');
}

try {
  const { ws, evaluate } = await attach();
  const version = await (await fetch(`http://127.0.0.1:${cdpPort}/json/version`)).json();
  console.log(`${version.Browser} (V8 ${version['V8-Version']})`);
  const started = Date.now();
  if (pin) {
    while ((await evaluate('document.title')) !== 'waiting') {
      if (Date.now() - started > 60000) throw new Error('the runtime never started');
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    console.log(execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(here, 'pin-chrome.ps1'),
      '-ProfileDir', profile], { encoding: 'utf8' }).trim());
    await evaluate('window.go = true');
  }
  while (!(await evaluate(`document.title.startsWith('done')`))) {
    if (Date.now() - started > 270000) throw new Error('timed out');
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  console.log(await evaluate('document.title'));
  console.log(await evaluate('window.benchOutput'));
  ws.close();
} finally {
  chrome.kill();
  server.kill();
  // Chrome holds its profile until it has exited: delete it a moment later.
  setTimeout(() => rmSync(profile, { recursive: true, force: true }), 1500);
}
