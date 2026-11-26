# Crc32C from C++, Python, Rust, Go and Zig

[`samples/callers`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/callers) holds one
small program per language that calls the [Crc32C](crc32c.md) library. None of them declares the
functions from memory. C++, Go and Zig read `crc32c.h`, the header the SDK writes from the
library's `PROTO` lines. Rust and Python state the same signatures, Rust in an `extern` block and
Python as `ctypes` types. Each program prints the same four lines and exits with 0 when all of
them hold:

```text
crc32c("123456789")          0xe3069283
in two calls                 0xe3069283
1 MiB                        0xd047105b
path on this CPU             3, hardware, 3-way (CRC32 + carry-less multiply)
```

- **`crc32c("123456789")`** is CRC-32C's standard check value.
- **In two calls:** the same bytes as `"1234"`, then `"56789"` continued from the first result.
  A `crc` of 0 starts a checksum, and a previous result continues one.
- **1 MiB** (byte *i* = *i* × 31 mod 251) on the path this CPU runs, checked against
  `crc32c_append_portable`, the table path. The two must agree.
- **The path:** what `crc32c_implementation()` returns: 1 portable, 2 hardware, 3 hardware
  with the 3-way kernel.

## What every caller starts from

`dotnet build samples/Crc32C -c Release` writes each RID's build into
`bin/Release/freestanding/<rid>/`. Next to the library is `crc32c.h`:

```c
/*
 * crc32c.h: the C declarations of crc32c, written by AsmFoundation.Sdk 0.4.0
 * from its PROTO and STRUCT lines (crc32c.api.json). Do not edit: the next build writes it again.
 */
#ifndef CRC32C_H
#define CRC32C_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* crc32c */
uint32_t crc32c_append(uint32_t crc, const uint8_t* data, size_t length);
uint32_t crc32c_append_hardware(uint32_t crc, const uint8_t* data, size_t length);
uint32_t crc32c_append_hardware_3way(uint32_t crc, const uint8_t* data, size_t length);
uint32_t crc32c_append_portable(uint32_t crc, const uint8_t* data, size_t length);
int32_t crc32c_implementation(void);

#ifdef __cplusplus
}
#endif

#endif /* CRC32C_H */
```

| | Linux | macOS | Windows |
|---|---|---|---|
| The library | `libcrc32c.so` | `libcrc32c.dylib`, install name `@rpath/libcrc32c.dylib` | `crc32c.dll` |
| What the linker takes | `-lcrc32c` | `-lcrc32c` | the import library `crc32c.dll.lib` |
| How a program finds it | its run path (`-Wl,-rpath,<folder>`) | its run path | beside the program, or on `PATH` |

The library depends on nothing, not even the C runtime. It loads into any process on its
platform, whatever language that process is written in.

## C++

The header's `extern "C"` block makes it usable from C++ as it is:

```cpp
#include "crc32c.h"

std::string_view check = "123456789";
const auto* bytes = reinterpret_cast<const std::uint8_t*>(check.data());
std::uint32_t whole = crc32c_append(0, bytes, check.size());   // 0xE3069283
```

```sh
LIB=../../Crc32C/bin/Release/freestanding/linux-x64
c++ -std=c++17 -Wall -Wextra -Werror -I"$LIB" main.cpp -L"$LIB" -lcrc32c -Wl,-rpath,"$LIB" -o crc32c-cpp
```

On macOS the same line works with the `osx-arm64` or `osx-x64` folder. With MSVC, compile with
`/I<folder>` and link `<folder>\crc32c.dll.lib`, as [C and other
languages](../guide/interop.md#c-and-other-languages) shows for C.

## Python: a package with the library in its wheel

`crc32c_asm` wraps the library with `ctypes`, so it needs no compiler and no extension module. Its
wheel carries the library next to the module:

```python
def _load() -> ctypes.CDLL:
    library = ctypes.CDLL(_library_path())   # libcrc32c.so, libcrc32c.dylib or crc32c.dll beside __init__.py
    for function in (library.crc32c_append, library.crc32c_append_portable):
        function.argtypes = [ctypes.c_uint32, ctypes.c_void_p, ctypes.c_size_t]
        function.restype = ctypes.c_uint32
    ...

def crc32c(data, crc: int = 0) -> int:
    return _call(_LIBRARY.crc32c_append, data, crc)   # any bytes-like object, read in place
```

To build the wheel:

1. Copy the platform's library into `src/crc32c_asm/`.
2. Run `python -m pip wheel . --no-deps -w dist`.

`setup.py` tags the wheel `py3-none-<platform>`, for example `py3-none-linux_x86_64` or
`py3-none-win_amd64`:

- **`py3-none`:** any CPython 3 runs it, because `ctypes` needs no particular Python ABI.
- **The platform:** pip installs it only where its library runs. Untagged, the wheel would claim
  `py3-none-any`, and pip would install a Linux library on Windows.

`python -m crc32c_asm` then runs the checks through the installed package.
[`python/README.md`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/callers/python)
has the steps, and [Python](../guide/interop.md#python) covers `ctypes` in general.

## Rust

`build.rs` links the library from the folder `CRC32C_LIB_DIR` names, and the program declares the
header's functions:

```rust
unsafe extern "C" {
    fn crc32c_append(crc: u32, data: *const u8, length: usize) -> u32;
    fn crc32c_implementation() -> i32;
}

fn crc32c(crc: u32, data: &[u8]) -> u32 {
    // SAFETY: data is a live slice of data.len() bytes, and the function only reads them.
    unsafe { crc32c_append(crc, data.as_ptr(), data.len()) }
}
```

```sh
CRC32C_LIB_DIR=../../Crc32C/bin/Release/freestanding/linux-x64 cargo run --release
```

- **Linux and macOS:** `build.rs` also puts the folder into the program's run path.
- **Windows:** it links the import library by its full name, `dylib:+verbatim=crc32c.dll.lib`.
  Plain `crc32c` would look for `crc32c.lib`. The program, `cargo run` included, then finds
  `crc32c.dll` only beside it or on `PATH`: Cargo adds no link folder outside its target
  directory to `PATH`.

[bindgen](https://rust-lang.github.io/rust-bindgen/) would generate the same `extern` block
from `crc32c.h`.

## Go

cgo includes the header and links the library. The sample names no build folder: the folders
come from `CGO_CFLAGS` and `CGO_LDFLAGS`.

```go
/*
#cgo LDFLAGS: -lcrc32c
#include "crc32c.h"
*/
import "C"

func crc32c(crc uint32, data []byte) uint32 {
	return uint32(C.crc32c_append(C.uint32_t(crc), bytesPointer(data), C.size_t(len(data))))
}
```

```sh
LIB=$(realpath ../../Crc32C/bin/Release/freestanding/linux-x64)
CGO_CFLAGS="-I$LIB" CGO_LDFLAGS="-L$LIB -Wl,-rpath,$LIB" go run .
```

A `[]byte` goes to the library in place, which cgo allows because the memory holds no Go
pointers and the library keeps none after it returns. cgo needs a C compiler (gcc or clang) on
`PATH`.

## Zig

`build.zig` translates `crc32c.h` into a module with Zig's translate-c, then links the library.
The program imports the module, so the declarations come from the header:

```zig
const header = b.addTranslateC(.{
    .root_source_file = .{ .cwd_relative = b.pathJoin(&.{ folder, "crc32c.h" }) },
    .target = target,
    .optimize = optimize,
    .link_libc = true,   // crc32c.h includes stdint.h and stddef.h
});
// ... .imports = &.{.{ .name = "crc32c", .module = header.createModule() }}
module.linkSystemLibrary("crc32c", .{});
```

```zig
const c = @import("crc32c");

fn crc32c(crc: u32, data: []const u8) u32 {
    return c.crc32c_append(crc, data.ptr, data.len);
}
```

```sh
zig build run -Dcrc32c=$(realpath ../../Crc32C/bin/Release/freestanding/linux-x64)
```

The sample is written for Zig 0.17. That release's `main` takes a `std.process.Init`, and its
standard output goes through `std.Io`, so earlier releases do not build it.

## How it is checked

[`CallerTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/CallerTests.cs)
builds each caller against the machine's own build of the library, the one the samples build
made, runs it, and checks the four lines. 0xd047105b checks the value itself, besides the
caller's own comparison with the portable path. The Python test also checks the wheel's name,
that the library is at the wheel's root, and that the wheel is not marked pure.

- **CI's Linux x64 machine runs all five** with `ASMSDK_TEST_CALLERS=required`, so a missing tool
  fails there instead of skipping. Its image has g++, Python, Rust and Go, and the workflow
  installs Zig 0.17.1, checked against its SHA-256.
- **Every other machine runs the callers whose tools it has** (`auto`), and skips the rest with
  the reason.
  - The C++ and Go callers need a C or C++ compiler on `PATH`, which Windows lacks in its default
    environment, so Windows skips them.
  - A tool built for another CPU than the machine's is skipped too, for example an x64 Python on
    an ARM64 machine: it could not load the machine's library.
- **Checked by hand** on Windows 11 x64:
  - the C++ caller with MSVC 2022 (`cl /std:c++17 /W4 /WX`, linked with `crc32c.dll.lib`);
  - the Rust caller with Rust 1.97;
  - the Python wheel (`py3-none-win_amd64`) with CPython 3.12.
