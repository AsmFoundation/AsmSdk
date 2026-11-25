using System.Reflection;

// ApiProto's baseline as the project file shaped it: Shapes.Native.Api holds every function (Flat),
// as public instance methods of a public sealed class.
var api = new Shapes.Native.Api();
Console.WriteLine($"Api.AddInts {api.AddInts(40, 2)} Api.Answer {api.Answer()}");
Type type = typeof(Shapes.Native.Api);
MethodInfo addInts = type.GetMethod("AddInts")!;
Console.WriteLine($"public {type.IsPublic} sealed {type.IsSealed} static {type.IsAbstract && type.IsSealed} instance method {!addInts.IsStatic} virtual {addInts.IsVirtual}");
