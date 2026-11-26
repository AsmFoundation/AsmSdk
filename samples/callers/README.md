# Callers: Crc32C from other languages

One small program per language that calls [Crc32C](../Crc32C), the assembly library, through
`crc32c.h`, the header AsmFoundation.Sdk writes from the library's `PROTO` lines. The
documentation walks through them in
[Crc32C from C++, Python, Rust, Go and Zig](https://asmfoundation.github.io/AsmSdk/examples/callers.html).

| Folder | Language | How it binds | Needs |
|---|---|---|---|
| [`cpp/`](cpp/main.cpp) | C++17 | `#include "crc32c.h"`, `-lcrc32c` | a C++ compiler |
| [`python/`](python) | Python | `ctypes`, in a package whose wheel carries the library (`py3-none-<platform>`) | Python 3 with pip |
| [`rust/`](rust) | Rust (edition 2024) | an `extern "C"` block; `build.rs` links the library | Rust 1.85 or later |
| [`go/`](go/main.go) | Go | cgo: `#include "crc32c.h"`, `#cgo LDFLAGS: -lcrc32c` | Go 1.21 or later, and gcc or clang for cgo |
| [`zig/`](zig) | Zig | `build.zig` translates `crc32c.h` (translate-c) and links the library | Zig 0.17 |

Each one prints the same four lines and exits with 0 when they all hold:

```text
crc32c("123456789")          0xe3069283
in two calls                 0xe3069283
1 MiB                        0xd047105b
path on this CPU             3, hardware, 3-way (CRC32 + carry-less multiply)
```

## Build and run them (Linux x64)

```sh
dotnet build ../Crc32C -c Release        # from samples/callers; the library and crc32c.h for every RID
LIB=$(realpath ../Crc32C/bin/Release/freestanding/linux-x64)

(cd cpp && c++ -std=c++17 -Wall -Wextra -Werror -I"$LIB" main.cpp -L"$LIB" -lcrc32c -Wl,-rpath,"$LIB" -o crc32c-cpp && ./crc32c-cpp)
(cd rust && CRC32C_LIB_DIR="$LIB" cargo run --release)
(cd go && CGO_CFLAGS="-I$LIB" CGO_LDFLAGS="-L$LIB -Wl,-rpath,$LIB" go run .)
(cd zig && zig build run -Dcrc32c="$LIB")
```

The Python package builds a wheel; [`python/README.md`](python/README.md) has its steps.

On macOS, use the `osx-arm64` or `osx-x64` folder. The library's install name is
`@rpath/libcrc32c.dylib`, so the same run path works.

On Windows the linker takes the import library `crc32c.dll.lib`, and the program finds
`crc32c.dll` beside it or on `PATH`:

- C++ with MSVC: `cl /std:c++17 /I<folder> main.cpp /link <folder>\crc32c.dll.lib`.
- Rust: `build.rs` links the import library by that name.

The samples build against the SDK of this checkout, through the local feed the end-to-end tests
fill (see [`samples/nuget.config`](../nuget.config)). To build Crc32C against the published SDK
instead, copy its folder next to a `global.json` that names `AsmFoundation.Sdk` and its version.

`tests/AsmSdk.Tests` (`CallerTests`) builds and runs all five on every CI run on Linux x64. Other
machines run the callers whose tools they have.
