#:project Lib/Lib.asmproj

// A C# file-based app (dotnet run app.cs): the #:project directive references the asm library
// as a ProjectReference would, so the library is built and delivered, and its PROTO line gives
// this file the same generated binding a .csproj gets.
Console.WriteLine($"file-based app: add_ints(20, 22) = {Lib.Add.AddInts(20, 22)}");
