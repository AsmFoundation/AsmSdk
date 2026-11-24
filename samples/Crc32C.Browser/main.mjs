// main.mjs - Crc32C.Browser's entry (WasmMainJSPath), in Node or in a browser.
//   node main.mjs verify | bench    runs Main with the argument (dotnet run passes it here), and
//                                   exits with Main's exit code
//   node main.mjs hash <file>       the page's report for a file: each implementation's checksum
//                                   and MB/s, through the same [JSExport] call (Page.Hash); exit
//                                   code 0 when the three agree, 1 when not or when the file
//                                   cannot be read, 2 for a usage error
//   in a browser (index.html)       the page: hashes the file it is given
// The page and "hash" start the runtime without running Main, and call the exported method.
import { dotnet } from './_framework/dotnet.js';

const inNode = typeof process === 'object' && typeof process.versions?.node === 'string';

/**
 * Starts the .NET runtime without running Main and returns the main assembly's [JSExport]
 * methods, by namespace and class: Page.Hash here (Program.cs has no namespace).
 * @returns {Promise<object>} The exports object.
 * @throws {Error} When the runtime cannot start: a file of _framework missing or unreachable.
 */
async function startExports() {
  const runtime = await dotnet.create();
  return runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
}

/**
 * Prints a Page.Hash report as a small table, for "node main.mjs hash".
 * @param {string} file The file's name as given on the command line.
 * @param {object} report The parsed JSON Page.Hash returned.
 */
function printReport(file, report) {
  console.log(`${file}: ${report.bytes} bytes (${report.compilation}, libcrc32c path ${report.path})`);
  for (const result of report.results) {
    console.log(`  ${result.name.padEnd(28)} ${result.crc}  ${result.mbps.toFixed(1).padStart(9)} MB/s`);
  }
  console.log(report.agree ? 'the three agree' : 'THE CHECKSUMS DIFFER');
}

/**
 * Wires index.html up: a file chosen or dropped is read, hashed and shown in the table. A runtime
 * that does not start is reported on the page, not thrown.
 * @returns {Promise<void>} Settles once the page is ready for a file (or has said why not).
 */
async function runPage() {
  const status = document.getElementById('status');
  const input = document.getElementById('file');
  const zone = document.getElementById('zone');
  const results = document.getElementById('results');
  const rows = document.getElementById('rows');
  const verdict = document.getElementById('verdict');

  let exports;
  try {
    exports = await startExports();
  } catch (error) {
    status.textContent = `The .NET runtime did not start: ${error.message}`;
    status.dataset.state = 'error';
    return;
  }

  status.textContent = 'Ready: choose a file, or drop one on the box.';
  input.disabled = false;

  /**
   * Hashes one file and fills the table; the page's thread is busy while .NET hashes. A file that
   * cannot be read or hashed is reported in the status line, not thrown.
   * @param {File} file The file chosen or dropped.
   * @returns {Promise<void>} Settles when the table (or the error) is shown.
   */
  async function hashFile(file) {
    status.dataset.state = 'busy';
    status.textContent = `Hashing ${file.name} (${file.size.toLocaleString('en')} bytes)…`;
    results.hidden = true;
    // Let the browser paint the message before the synchronous call takes the thread.
    await new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve)));
    try {
      const bytes = new Uint8Array(await file.arrayBuffer());
      const report = JSON.parse(exports.Page.Hash(bytes));
      const fastest = Math.max(...report.results.map((r) => r.mbps));
      rows.replaceChildren(...report.results.map((r) => {
        const row = document.createElement('tr');
        const share = fastest > 0 ? (r.mbps / fastest) * 100 : 0;
        row.innerHTML = '<th scope="row"></th><td class="crc"></td><td class="speed"><span class="bar"></span><span class="mbps"></span></td>';
        row.querySelector('th').textContent = r.name;
        row.querySelector('.crc').textContent = r.crc;
        row.querySelector('.mbps').textContent = `${r.mbps.toFixed(1)} MB/s`;
        row.querySelector('.bar').style.inlineSize = `${share.toFixed(1)}%`;
        return row;
      }));
      verdict.textContent = report.agree
        ? `All three agree: CRC-32C ${report.results[0].crc}.`
        : 'The checksums differ: one implementation is wrong.';
      verdict.dataset.state = report.agree ? 'ok' : 'error';
      status.dataset.state = '';
      status.textContent = `${file.name}: ${report.bytes.toLocaleString('en')} bytes, C# ${report.compilation === 'AOT' ? 'compiled ahead of time' : 'interpreted'}, libcrc32c path ${report.path}.`;
      results.hidden = false;
    } catch (error) {
      status.dataset.state = 'error';
      status.textContent = `${file.name} could not be hashed: ${error.message}`;
    }
  }

  input.addEventListener('change', () => {
    if (input.files.length > 0) hashFile(input.files[0]);
  });
  zone.addEventListener('dragover', (event) => {
    event.preventDefault();
    zone.dataset.over = 'true';
  });
  zone.addEventListener('dragleave', () => delete zone.dataset.over);
  zone.addEventListener('drop', (event) => {
    event.preventDefault();
    delete zone.dataset.over;
    if (event.dataTransfer.files.length > 0) hashFile(event.dataTransfer.files[0]);
  });
}

if (inNode) {
  const args = process.argv.slice(2);
  if (args[0] === 'hash') {
    if (args.length !== 2) {
      console.error('usage: node main.mjs hash <file>');
      process.exit(2);
    }

    // Node's own file system: the .NET runtime sees only its in-memory one, so the bytes are
    // read here and handed over, as the page does with the file it is given.
    const { readFile } = await import('node:fs/promises');
    let bytes;
    try {
      bytes = new Uint8Array(await readFile(args[1]));
    } catch (error) {
      console.error(`hash: ${args[1]}: ${error.message}`);
      process.exit(1);
    }

    const exports = await startExports();
    const report = JSON.parse(exports.Page.Hash(bytes));
    printReport(args[1], report);
    process.exit(report.agree ? 0 : 1);
  }

  const { runMainAndExit } = await dotnet.withApplicationArguments(...args).create();
  await runMainAndExit();
} else {
  await runPage();
}
