// The browser-wasm app's JavaScript entry point (WasmMainJSPath), here run by Node from the
// build's AppBundle folder: start the .NET runtime, run Main, exit with its exit code.
import { dotnet } from './_framework/dotnet.js'

const { runMainAndExit } = await dotnet.create();
await runMainAndExit();
