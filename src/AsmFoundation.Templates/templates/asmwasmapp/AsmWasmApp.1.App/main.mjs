// main.mjs - the app's JavaScript entry point (WasmMainJSPath): starts the .NET runtime, runs
// Main, and exits with its exit code. "dotnet run" runs it in Node; a page loads the same
// _framework/dotnet.js to run the app in a browser.
import { dotnet } from './_framework/dotnet.js';

const { runMainAndExit } = await dotnet.create();
await runMainAndExit();
