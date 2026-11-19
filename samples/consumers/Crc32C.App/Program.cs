using System.Text;
using Samples.Hashing;

namespace Samples.Hashing.App;

/// <summary>
/// crc32c-app: prints the CRC-32C of each file named on the command line, computed by the
/// assembly library that came with the Samples.Crc32C.Net package.
/// </summary>
internal static class Program
{
    /// <summary>The size of each read: large enough that the library's 3-way kernel does most of the work.</summary>
    private const int ChunkSize = 1 << 20;

    /// <summary>Checksums the files, or shows the check value and the path in use when given none.</summary>
    /// <param name="args">File names; none for the self-description.</param>
    /// <returns>0 on success; 1 when a file could not be read (the others are still printed).</returns>
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            uint check = Crc32C.HashToUInt32(Encoding.ASCII.GetBytes("123456789"));
            Console.WriteLine($"crc32c(\"123456789\") = {check:x8}, computed in assembly on the {Crc32C.Path} path");
            return check == 0xE3069283 ? 0 : 1;
        }

        int status = 0;
        byte[] buffer = new byte[ChunkSize];
        foreach (string name in args)
        {
            try
            {
                using FileStream file = File.OpenRead(name);
                uint crc = 0;
                int read;
                while ((read = file.Read(buffer)) > 0)
                {
                    // Append continues the checksum, so the file never has to fit in memory.
                    crc = Crc32C.Append(crc, buffer.AsSpan(0, read));
                }

                Console.WriteLine($"{crc:x8}  {name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The same "program: name: reason" shape as the other tools; keep going.
                Console.Error.WriteLine($"crc32c-app: {name}: {ex.Message}");
                status = 1;
            }
        }

        return status;
    }
}
