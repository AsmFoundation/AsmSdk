namespace NetProgramLibrary;

/// <summary>The library's own code: the name of the program its package carries.</summary>
public static class Helper
{
    /// <summary>The program's file name on this OS.</summary>
    public static string ProgramFile => OperatingSystem.IsWindows() ? "everywhere-program.exe" : "everywhere-program";
}
