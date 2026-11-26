# crc32c-asm

CRC-32C from [samples/Crc32C](../../Crc32C), an assembly library built by AsmFoundation.Sdk,
as a Python package. It uses `ctypes`, so it needs no compiler and no extension module, and its
wheel carries the library.

```python
import crc32c_asm

crc32c_asm.crc32c(b"123456789")          # 0xE3069283
crc32c_asm.crc32c(b"56789", crc32c_asm.crc32c(b"1234"))   # continued: the same value
crc32c_asm.implementation()              # 3: hardware, 3-way, on a CPU that has it
```

Any bytes-like object is read in place, without a copy (`bytes`, `bytearray`, a writable
`memoryview`).

## Build the wheel

1. Build the library: `dotnet build ../../Crc32C -c Release`.
2. Copy this platform's library next to `src/crc32c_asm/__init__.py`. On Linux x64 that is
   `../../Crc32C/bin/Release/freestanding/linux-x64/libcrc32c.so`; on macOS it is
   `libcrc32c.dylib` from `osx-arm64/` or `osx-x64/`, and on Windows `crc32c.dll` from
   `win-x64/` or `win-arm64/`.
3. Build the wheel with `python -m pip wheel . --no-deps -w dist`, then install it with
   `pip install dist/*.whl`.
4. Check it: `python -m crc32c_asm` runs the same checks as the other callers.

The wheel is tagged `py3-none-<platform>`, for example `py3-none-linux_x86_64`:

- It runs on any CPython 3, because `ctypes` needs no particular Python ABI.
- It installs only on the platform whose library it carries. `setup.py` sets that tag. Without
  it the wheel would claim `py3-none-any`, and pip would install a Linux library on Windows.

Publishing on PyPI would also need a manylinux tag. The library depends on nothing, not even
libc, so `auditwheel` has nothing to graft.

`CRC32C_LIBRARY=/path/to/libcrc32c.so` loads another copy instead of the packed one, for
example while you work on the library.
