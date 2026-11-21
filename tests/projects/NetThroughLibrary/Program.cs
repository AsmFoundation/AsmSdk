// The asm library is NetLibrary's business: this program only prints what it answers. A
// DllNotFoundException here means the build or publish left the everywhere library out.
Console.WriteLine(NetLibrary.EverywhereInfo.Describe());
