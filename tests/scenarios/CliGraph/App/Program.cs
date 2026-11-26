// CliGraph's app: one call into the asm library, through the binding the SDK generates.
Console.WriteLine($"add_ints(40, 2) = {Lib.Add.AddInts(40, 2)}");
