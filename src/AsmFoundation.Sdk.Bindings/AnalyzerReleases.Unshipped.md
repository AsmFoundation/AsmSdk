; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ASM0140 | AsmFoundation.Bindings | Error | BindingsGenerator: [AsmLibrary] names no referenced asm library with PROTO or STRUCT lines
ASM0141 | AsmFoundation.Bindings | Error | BindingsGenerator: a Files or Folder entry is not one of the library's
ASM0142 | AsmFoundation.Bindings | Error | BindingsGenerator: a function bound by a second of the app's classes without AliasBinding
ASM0143 | AsmFoundation.Bindings | Error | BindingsGenerator: a class that cannot hold bindings (not partial, generic), or a name generated twice
ASM0144 | AsmFoundation.Bindings | Error | BindingsGenerator: an API file that cannot be read
ASM0145 | AsmFoundation.Bindings | Error | BindingsGenerator: a public copy of a function taking a struct the internal baseline holds
ASM0146 | AsmFoundation.Bindings | Error | BindingsGenerator: shaping options that do not fit together, or a value that is no option's
ASM0147 | AsmFoundation.Bindings | Warning | BindingsGenerator: the project's C# is older than 11; no bindings generated
