namespace NetRidChoice;

/// <summary>
/// The library's one type: a .NET project with no source would warn CS2008, an error under the
/// test projects' TreatWarningsAsErrors. What is tested is the asm reference.
/// </summary>
public static class Marker
{
    /// <summary>The name of the asm library this project references (RidNone's AssemblyName).</summary>
    public const string AsmLibrary = "ridnone";
}
