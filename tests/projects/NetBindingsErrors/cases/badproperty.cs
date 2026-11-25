namespace NetBindingsErrors;

/// <summary>
/// The project file's case (NetBindingsErrors.csproj sets AsmBindingsVisibility to Publik for it):
/// no code mentions the attribute, so the SDK writes the baseline and its task reports ASM0146.
/// </summary>
internal static class BadProperty
{
}
