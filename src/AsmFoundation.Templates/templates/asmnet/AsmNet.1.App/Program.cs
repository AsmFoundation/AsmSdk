// Calls add_ints, a function written in assembly in AsmNet.1.Native, and prints its answer.
// AsmNet._1.Native.Add is the C# binding the SDK writes from the PROTO line in add.asm: the
// namespace of the library, the class of the file add.asm, the method of add_ints.
int sum = AsmNet._1.Native.Add.AddInts(40, 2);
Console.WriteLine($"add_ints(40, 2) = {sum}");
