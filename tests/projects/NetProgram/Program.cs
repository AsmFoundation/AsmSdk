// NetProgram (and NetProgramPackage, which compiles this file too): starts the asm program
// everywhere-program, which a ProjectReference or a package delivered, and returns its exit
// status (10 * ew_platform() + the number of arguments), so a test sees which RID's build ran.
//
// Where to look: the .NET host's native search folders (NATIVE_DLL_SEARCH_DIRECTORIES), which
// hold every folder that deps.json names for a native file of this RID, wherever
// BinariesCopyPath put it, then the app's own folder (a RID-specific build, or an app without
// deps.json). This is the documented way to find a delivered program.
using System.Diagnostics;

string file = OperatingSystem.IsWindows() ? "everywhere-program.exe" : "everywhere-program";
var folders = new List<string>();
if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string list)
{
    folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
}

folders.Add(AppContext.BaseDirectory);
string? program = folders.Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);
if (program is null)
{
    Console.WriteLine($"{file} is in none of: {string.Join(" | ", folders)}");
    return 100;
}

var start = new ProcessStartInfo(program) { RedirectStandardOutput = true };
start.ArgumentList.Add("from .NET");
using Process process = Process.Start(start)!;
Console.Write(process.StandardOutput.ReadToEnd());
process.WaitForExit();
return process.ExitCode;
