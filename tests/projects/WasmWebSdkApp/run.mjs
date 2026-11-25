// Runs WasmWebSdkApp's Main under Node: a Microsoft.NET.Sdk.WebAssembly app has no WasmMainJSPath
// script of its own (its page would start it), so this one stands in for the page, from outside
// the app so that it is never deployed with it. Usage: node run.mjs <the build's wwwroot folder>.
// It starts the .NET runtime from that folder's _framework/dotnet.js (the boot configuration is
// inside it, and the fingerprinted files are found next to it), runs Main and exits with its
// exit code.
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';

const wwwroot = process.argv[2];
if (!wwwroot) {
  console.error('usage: node run.mjs <wwwroot folder>');
  process.exit(2);
}

const { dotnet } = await import(pathToFileURL(join(resolve(wwwroot), '_framework', 'dotnet.js')).href);
const { runMainAndExit } = await dotnet.create();
await runMainAndExit();
