"""crc32c_asm: CRC-32C from samples/Crc32C, an assembly library built by AsmFoundation.Sdk, wrapped
for Python with ctypes (no compiler, no extension module).

The wheel carries the library itself (libcrc32c.so, libcrc32c.dylib or crc32c.dll) next to this
file, so `pip install` is all a user does. CRC32C_LIBRARY, when set, names another copy to load
instead, for a library built locally.

    >>> import crc32c_asm
    >>> hex(crc32c_asm.crc32c(b"123456789"))
    '0xe3069283'
"""

import ctypes
import os
import sys

__all__ = ["crc32c", "crc32c_portable", "implementation", "IMPLEMENTATIONS"]

# What implementation() returns, as crc32c.inc defines it.
IMPLEMENTATIONS = {
    1: "portable (table)",
    2: "hardware (CRC32 instruction)",
    3: "hardware, 3-way (CRC32 + carry-less multiply)",
}


def _library_path() -> str:
    """The file to load: CRC32C_LIBRARY when set, else the copy packed next to this module.

    Returns:
        An absolute path; whether it exists is ctypes' to report.
    """
    override = os.environ.get("CRC32C_LIBRARY")
    if override:
        return os.path.abspath(override)
    if sys.platform == "win32":
        name = "crc32c.dll"
    elif sys.platform == "darwin":
        name = "libcrc32c.dylib"
    else:
        name = "libcrc32c.so"
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), name)


def _load() -> ctypes.CDLL:
    """Loads the library and declares the three functions this module calls, with the types of
    their PROTO lines (crc32c.h), so ctypes converts and checks every argument.

    Returns:
        The loaded library.

    Raises:
        OSError: The file is missing, or built for another platform; the message names the path.
    """
    path = _library_path()
    try:
        library = ctypes.CDLL(path)
    except OSError as error:
        raise OSError(f"crc32c_asm could not load {path}: {error}") from error
    # uint32_t crc32c_append(uint32_t crc, const uint8_t* data, size_t length), and the same for
    # the portable path. data is c_void_p: ctypes passes a bytes object's own storage for it, and
    # _call passes any other buffer's address.
    for function in (library.crc32c_append, library.crc32c_append_portable):
        function.argtypes = [ctypes.c_uint32, ctypes.c_void_p, ctypes.c_size_t]
        function.restype = ctypes.c_uint32
    # int32_t crc32c_implementation(void)
    library.crc32c_implementation.argtypes = []
    library.crc32c_implementation.restype = ctypes.c_int32
    return library


_LIBRARY = _load()


def _call(function, data, crc: int) -> int:
    """Calls function(crc, data, len(data)) on any bytes-like object without copying it.

    Args:
        function: crc32c_append or crc32c_append_portable.
        data: bytes, bytearray, memoryview or anything else with the buffer protocol, read in
            place. The one copy is of a read-only buffer that is not bytes (a memoryview of
            bytes, an mmap opened read-only), which ctypes can't take the address of.
        crc: 0 to start a checksum, or a previous result to continue it.

    Returns:
        The checksum, 0..2**32-1.

    Raises:
        ValueError: crc is outside 0..2**32-1.
        TypeError: data does not support the buffer protocol, or is not contiguous.
    """
    if not 0 <= crc <= 0xFFFFFFFF:
        raise ValueError(f"crc must be a 32-bit unsigned value, got {crc}")
    view = memoryview(data).cast("B")
    if view.nbytes == 0:
        return function(crc, None, 0)
    if view.readonly:
        # c_void_p passes a bytes object's own storage; the library only reads it.
        return function(crc, data if isinstance(data, bytes) else view.tobytes(), view.nbytes)
    return function(crc, ctypes.addressof(ctypes.c_char.from_buffer(view)), view.nbytes)


def crc32c(data, crc: int = 0) -> int:
    """CRC-32C (Castagnoli) of data on the fastest path this CPU has, continued from crc.

    Args:
        data: Any bytes-like object; it is read in place, not copied.
        crc: 0 for a new checksum, or a previous result to continue it over more data.

    Returns:
        The checksum, 0..2**32-1. crc32c(b"123456789") is 0xE3069283.

    Raises:
        ValueError: crc is outside 0..2**32-1.
        TypeError: data does not support the buffer protocol.
    """
    return _call(_LIBRARY.crc32c_append, data, crc)


def crc32c_portable(data, crc: int = 0) -> int:
    """The same checksum on the library's table path, whatever the CPU: for checking the fast
    path against, as the library's own tests do.

    Args:
        data: Any bytes-like object; it is read in place, not copied.
        crc: 0 for a new checksum, or a previous result to continue it over more data.

    Returns:
        The checksum, 0..2**32-1, always equal to crc32c(data, crc).

    Raises:
        ValueError: crc is outside 0..2**32-1.
        TypeError: data does not support the buffer protocol.
    """
    return _call(_LIBRARY.crc32c_append_portable, data, crc)


def implementation() -> int:
    """Which path crc32c() runs on this CPU; it never changes during a process.

    Returns:
        1, 2 or 3; IMPLEMENTATIONS names them.
    """
    return _LIBRARY.crc32c_implementation()
