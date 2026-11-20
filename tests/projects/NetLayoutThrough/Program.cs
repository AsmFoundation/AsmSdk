// The asm library is NetLayoutLibrary's business: this program only prints what it answers. A
// DllNotFoundException here means the build or publish left the everywhere library where
// nothing looks: no deps.json entry and no generated loader for its folder.
Console.WriteLine(NetLayoutLibrary.EverywhereInfo.Describe());
