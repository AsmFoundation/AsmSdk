// run.mjs - calls AsmWasm.1's module from Node: "dotnet build", then "node run.mjs".
// A browser loads the same file the same way (WebAssembly.instantiateStreaming(fetch(...))).
import { existsSync, readFileSync, statSync } from 'node:fs';

// "dotnet build" writes Debug and "dotnet build -c Release" Release: run the newer of the two.
const builds = ['Debug', 'Release']
  .map((configuration) => new URL(`./bin/${configuration}/freestanding/browser-wasm/AsmWasm.1.wasm`, import.meta.url))
  .filter((module) => existsSync(module))
  .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
if (builds.length === 0) {
  console.error('No module yet: run "dotnet build" first.');
  process.exit(1);
}

const { instance } = await WebAssembly.instantiate(readFileSync(builds[0]));
console.log(`add_ints(40, 2) = ${instance.exports.add_ints(40, 2)}`);
