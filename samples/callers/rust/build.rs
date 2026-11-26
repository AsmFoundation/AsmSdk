//! Links samples/Crc32C's library: CRC32C_LIB_DIR names the folder of this platform's build,
//! for example ../../Crc32C/bin/Release/freestanding/linux-x64.
//!
//! The folder goes on the linker's search path. On Linux and macOS it also goes into the
//! program's run path, so the program finds the shared library where it was built without
//! LD_LIBRARY_PATH. On Windows the linker takes crc32c.dll.lib, the import library the SDK
//! writes beside crc32c.dll (named after the DLL, so +verbatim: the usual `crc32c` would look
//! for crc32c.lib). Windows has no run path: the program, `cargo run` included, finds
//! crc32c.dll only beside it or on PATH (Cargo adds no link-search folder outside its target
//! directory to PATH).

use std::env;
use std::path::Path;

fn main() {
    println!("cargo::rerun-if-env-changed=CRC32C_LIB_DIR");
    // A missing variable is a build error that says what to set, rather than a link error
    // about a library the linker could not find.
    let dir = env::var("CRC32C_LIB_DIR").unwrap_or_else(|_| {
        panic!("set CRC32C_LIB_DIR to the folder of samples/Crc32C's build for this platform, \
                for example ../../Crc32C/bin/Release/freestanding/linux-x64")
    });
    let dir = Path::new(&dir).canonicalize().unwrap_or_else(|error| panic!("CRC32C_LIB_DIR {dir}: {error}"));
    println!("cargo::rerun-if-changed={}", dir.display());
    println!("cargo::rustc-link-search=native={}", dir.display());
    // CARGO_CFG_TARGET_OS is the target's OS (build.rs itself runs on the host), which is what
    // decides the library's file names and the linker's syntax.
    match env::var("CARGO_CFG_TARGET_OS").as_deref() {
        Ok("windows") => println!("cargo::rustc-link-lib=dylib:+verbatim=crc32c.dll.lib"),
        Ok("linux") | Ok("macos") => {
            println!("cargo::rustc-link-lib=dylib=crc32c");
            println!("cargo::rustc-link-arg=-Wl,-rpath,{}", dir.display());
        }
        _ => println!("cargo::rustc-link-lib=dylib=crc32c"),
    }
}
