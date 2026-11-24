; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ASM0140 | AsmFoundation.Bindings | Error | BindingsGenerator: [AsmLibrary] names no referenced asm library with PROTO lines
ASM0141 | AsmFoundation.Bindings | Error | BindingsGenerator: a Files or Folder entry is not one of the library's
ASM0142 | AsmFoundation.Bindings | Error | BindingsGenerator: a function bound by a second class without AliasBinding
ASM0143 | AsmFoundation.Bindings | Error | BindingsGenerator: a class that is not static partial, or a name generated twice
ASM0144 | AsmFoundation.Bindings | Error | BindingsGenerator: an API file that cannot be read
ASM0145 | AsmFoundation.Bindings | Error | BindingsGenerator: a public class binds a function taking a struct the internal default class holds
