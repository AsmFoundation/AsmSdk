// Calls add_ints of the library the asmlib template created (Demo.Extra), with numbers the
// asmnet app does not use, so the line printed can only come from this program. Demo.Extra.Add is
// the binding the SDK writes from the template's PROTO line (the library's namespace, the class
// of add.asm): the one ProjectReference line is all this program needs.
Console.WriteLine($"Demo.Extra add_ints(1000, 337) = {Demo.Extra.Add.AddInts(1000, 337)}");
