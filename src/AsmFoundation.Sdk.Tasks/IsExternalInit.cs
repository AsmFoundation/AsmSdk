namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets the C# compiler emit records and init-only setters for netstandard2.0, whose base library
/// predates the type. The compiler only needs the type to exist; it is internal so that it never
/// clashes with the real one in a newer runtime that loads this assembly.
/// </summary>
internal static class IsExternalInit
{
}
