"""python -m crc32c_asm: the same checks as the other callers in samples/callers, through the
installed package.

Exit code 0 when every check holds, 1 when one does not (each is printed).
"""

import sys

from . import IMPLEMENTATIONS, crc32c, crc32c_portable, implementation


def main() -> int:
    """Runs the checks and prints each one.

    Returns:
        The exit code.
    """
    failures = 0

    def check(what: str, got: int, want: int) -> None:
        """Prints one check, and counts it when it fails, so every failure is shown.

        Args:
            what: What was computed, as printed.
            got: The library's answer.
            want: The answer it must give.
        """
        nonlocal failures
        print(f"{what:<28} 0x{got:08x}" + ("" if got == want else "  FAILED"))
        if got != want:
            print(f"{'':<28} 0x{want:08x} expected")
            failures += 1

    # The standard check value, then the same bytes in two calls: crc 0 starts a checksum, and
    # a previous result continues one.
    check('crc32c("123456789")', crc32c(b"123456789"), 0xE3069283)
    check("in two calls", crc32c(b"56789", crc32c(b"1234")), 0xE3069283)

    # 1 MiB on the path this CPU runs, against the portable table path: every path must agree.
    # A bytearray, so the buffer goes to the library by address, without a copy.
    value = bytearray((i * 31) % 251 for i in range(1 << 20))
    check("1 MiB", crc32c(value), crc32c_portable(value))

    path = implementation()
    print(f"{'path on this CPU':<28} {path}, {IMPLEMENTATIONS.get(path, 'unknown')}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
