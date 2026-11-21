// Everything native here came from the Test.NetLayoutLibrary package: a DllNotFoundException
// means the package's binaries were moved to a folder that neither deps.json nor the
// package's generated loader names.
Console.WriteLine(NetLayoutLibrary.EverywhereInfo.Describe());
