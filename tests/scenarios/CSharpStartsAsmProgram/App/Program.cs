// Finds the asm program asm-helper, which the ProjectReference delivered, starts it with one
// argument, and prints what it printed and its exit status. Exit status 0 when the helper
// echoed the argument and returned 42 (40 + argc), 1 otherwise, 100 when it was not found.
//
// Where to look: the .NET host's native search folders (NATIVE_DLL_SEARCH_DIRECTORIES), which
// name every folder deps.json lists a native file of this RID in (runtimes/<rid>/native/ in a
// portable build), then the app's own folder (a RID-specific build or publish). This is how the
// AsmSdk documentation says to find a delivered program.
using System.Diagnostics;

string file = OperatingSystem.IsWindows() ? "asm-helper.exe" : "asm-helper";
var folders = new List<string>();
if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string list)
{
    folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
}

folders.Add(AppContext.BaseDirectory);
string? helper = folders.Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);
if (helper is null)
{
    Console.WriteLine($"{file} is in none of: {string.Join(" | ", folders)}");
    return 100;
}

const string Message = "hello from C#";
var start = new ProcessStartInfo(helper) { RedirectStandardOutput = true };
start.ArgumentList.Add(Message);
using Process process = Process.Start(start)!;
string printed = process.StandardOutput.ReadToEnd().Trim();
process.WaitForExit();
Console.WriteLine($"asm-helper printed \"{printed}\" and exited {process.ExitCode}");
return printed == Message && process.ExitCode == 42 ? 0 : 1;
