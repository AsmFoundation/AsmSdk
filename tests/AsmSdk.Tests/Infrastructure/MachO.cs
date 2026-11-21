using System.Buffers.Binary;
using System.Text;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// Reads what the bundled llvm-readobj does not print for Mach-O: the run paths (LC_RPATH) of a
/// 64-bit Mach-O file, which decide where dyld looks for an @rpath/ dylib.
/// </summary>
internal static class MachO
{
    /// <summary>The 64-bit Mach-O magic, little-endian (every macOS RID AsmSdk builds).</summary>
    private const uint Magic64 = 0xFEEDFACF;

    /// <summary>LC_RPATH: a run path (LC_REQ_DYLD | 0x1c).</summary>
    private const uint LoadCommandRunPath = 0x8000001C;

    /// <summary>The size of mach_header_64: the load commands follow it.</summary>
    private const int HeaderSize = 32;

    /// <summary>The run paths of a Mach-O file, in load-command order.</summary>
    /// <param name="path">The Mach-O file (a program or a dylib).</param>
    /// <returns>Each LC_RPATH's path, for example "@loader_path/lib/osx-arm64".</returns>
    /// <exception cref="InvalidDataException">The file is not a 64-bit little-endian Mach-O file, or a load command runs past its end.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static IReadOnlyList<string> RunPaths(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic64)
        {
            throw new InvalidDataException($"{path} is not a 64-bit Mach-O file");
        }

        uint commands = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        var runPaths = new List<string>();
        int offset = HeaderSize;
        for (uint i = 0; i < commands; i++)
        {
            if (offset + 8 > file.Length)
            {
                throw new InvalidDataException($"{path}: load command {i} runs past the end of the file");
            }

            uint command = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4));
            if (size < 8 || offset + size > file.Length)
            {
                throw new InvalidDataException($"{path}: load command {i} has a bad size ({size})");
            }

            if (command == LoadCommandRunPath)
            {
                // rpath_command: cmd, cmdsize, then lc_str, the path's offset from the command's
                // start; the path is NUL-terminated and padded to the command's size.
                int start = offset + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 8));
                int end = Array.IndexOf(file, (byte)0, start, offset + size - start);
                runPaths.Add(Encoding.UTF8.GetString(file, start, (end < 0 ? offset + size : end) - start));
            }

            offset += size;
        }

        return runPaths;
    }
}
