//! Crc32C from Rust: samples/Crc32C's assembly library, through the declarations of crc32c.h,
//! the header AsmFoundation.Sdk writes from the library's PROTO lines. They are written here as a
//! Rust extern block; bindgen would generate the same from the header.
//!
//! Build and run on Linux (README.md has macOS and Windows):
//!   dotnet build ../../Crc32C -c Release
//!   CRC32C_LIB_DIR=../../Crc32C/bin/Release/freestanding/linux-x64 cargo run --release
//!
//! Exit code 0 when every check below holds, 1 when one does not (each is printed).

use std::process::ExitCode;

// The PROTO lines of crc32c.asm, as crc32c.h declares them. build.rs links the library.
unsafe extern "C" {
    /// uint32_t crc32c_append(uint32_t crc, const uint8_t* data, size_t length)
    fn crc32c_append(crc: u32, data: *const u8, length: usize) -> u32;
    /// uint32_t crc32c_append_portable(uint32_t crc, const uint8_t* data, size_t length)
    fn crc32c_append_portable(crc: u32, data: *const u8, length: usize) -> u32;
    /// int32_t crc32c_implementation(void)
    fn crc32c_implementation() -> i32;
}

/// CRC-32C of data, continued from crc (0 starts a new checksum), on the fastest path this CPU
/// has.
///
/// Safe to call with any slice: the library reads exactly `data.len()` bytes from its start, and
/// keeps no pointer after it returns.
fn crc32c(crc: u32, data: &[u8]) -> u32 {
    // SAFETY: data is a live slice of data.len() bytes for the whole call, and the function only
    // reads them. An empty slice's pointer is dangling but non-null, and length 0 reads nothing.
    unsafe { crc32c_append(crc, data.as_ptr(), data.len()) }
}

/// The same checksum on the library's table path, whatever the CPU.
fn crc32c_portable(crc: u32, data: &[u8]) -> u32 {
    // SAFETY: as in crc32c.
    unsafe { crc32c_append_portable(crc, data.as_ptr(), data.len()) }
}

/// The library's name for what crc32c_implementation() returns; "unknown" for any other number.
fn path_name(path: i32) -> &'static str {
    match path {
        1 => "portable (table)",
        2 => "hardware (CRC32 instruction)",
        3 => "hardware, 3-way (CRC32 + carry-less multiply)",
        _ => "unknown",
    }
}

/// Prints one check, and returns whether it held, so every failure is shown, not just the first.
fn check(what: &str, got: u32, want: u32) -> bool {
    println!("{what:<28} 0x{got:08x}{}", if got == want { "" } else { "  FAILED" });
    if got != want {
        println!("{:<28} 0x{want:08x} expected", "");
    }
    got == want
}

fn main() -> ExitCode {
    let check_bytes = b"123456789";
    let mut ok = true;
    // The standard check value, then the same bytes in two calls.
    ok &= check("crc32c(\"123456789\")", crc32c(0, check_bytes), 0xE306_9283);
    ok &= check("in two calls", crc32c(crc32c(0, &check_bytes[..4]), &check_bytes[4..]), 0xE306_9283);

    // 1 MiB on the path this CPU runs, against the portable table path: every path must agree.
    let value: Vec<u8> = (0..1usize << 20).map(|i| (i * 31 % 251) as u8).collect();
    ok &= check("1 MiB", crc32c(0, &value), crc32c_portable(0, &value));

    // SAFETY: no arguments; the library picks and remembers its path on the first call.
    let path = unsafe { crc32c_implementation() };
    println!("{:<28} {path}, {}", "path on this CPU", path_name(path));
    if ok { ExitCode::SUCCESS } else { ExitCode::FAILURE }
}
