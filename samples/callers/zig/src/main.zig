//! Crc32C from Zig: samples/Crc32C's assembly library, through crc32c.h, the header
//! AsmFoundation.Sdk writes from the library's PROTO lines. build.zig translates the header into
//! the `crc32c` module and links the library.
//!
//! Exit code 0 when every check below holds, 1 when one does not (each is printed).

const std = @import("std");
const c = @import("crc32c");

/// CRC-32C of data, continued from crc (0 starts a new checksum), on the fastest path this CPU
/// has. The library reads exactly data.len bytes and keeps no pointer after it returns.
fn crc32c(crc: u32, data: []const u8) u32 {
    return c.crc32c_append(crc, data.ptr, data.len);
}

/// The same checksum on the library's table path, whatever the CPU.
fn crc32cPortable(crc: u32, data: []const u8) u32 {
    return c.crc32c_append_portable(crc, data.ptr, data.len);
}

/// The library's name for what crc32c_implementation() returns; "unknown" for any other number.
fn pathName(path: i32) []const u8 {
    return switch (path) {
        1 => "portable (table)",
        2 => "hardware (CRC32 instruction)",
        3 => "hardware, 3-way (CRC32 + carry-less multiply)",
        else => "unknown",
    };
}

/// Prints one check and returns whether it held, so every failure is shown, not just the first.
fn check(out: anytype, what: []const u8, got: u32, want: u32) !bool {
    try out.print("{s:<28} 0x{x:0>8}{s}\n", .{ what, got, if (got == want) "" else "  FAILED" });
    if (got != want) try out.print("{s:<28} 0x{x:0>8} expected\n", .{ "", want });
    return got == want;
}

/// Runs the checks. init brings Zig 0.17's I/O implementation, which stdout's writer needs, and
/// the general-purpose allocator for the 1 MiB buffer.
pub fn main(init: std.process.Init) !u8 {
    var buffer: [1024]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;

    var ok = true;
    const check_bytes = "123456789";
    // The standard check value, then the same bytes in two calls.
    ok = try check(out, "crc32c(\"123456789\")", crc32c(0, check_bytes), 0xE3069283) and ok;
    ok = try check(out, "in two calls", crc32c(crc32c(0, check_bytes[0..4]), check_bytes[4..]), 0xE3069283) and ok;

    // 1 MiB on the path this CPU runs, against the portable table path: every path must agree.
    const value = try init.gpa.alloc(u8, 1 << 20);
    defer init.gpa.free(value);
    for (value, 0..) |*byte, i| byte.* = @intCast(i * 31 % 251);
    ok = try check(out, "1 MiB", crc32c(0, value), crc32cPortable(0, value)) and ok;

    const path = c.crc32c_implementation();
    try out.print("{s:<28} {d}, {s}\n", .{ "path on this CPU", path, pathName(path) });
    try out.flush();
    return if (ok) 0 else 1;
}
