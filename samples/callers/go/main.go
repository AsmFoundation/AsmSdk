// Command crc32c-caller calls Crc32C from Go: samples/Crc32C's assembly library, through cgo and
// crc32c.h, the header AsmFoundation.Sdk writes from the library's PROTO lines.
//
// The header and the library are found through CGO_CFLAGS and CGO_LDFLAGS, so no path to a
// build folder is written here. Build and run on Linux (README.md has macOS):
//
//	dotnet build ../../Crc32C -c Release
//	LIB=$(realpath ../../Crc32C/bin/Release/freestanding/linux-x64)
//	CGO_CFLAGS="-I$LIB" CGO_LDFLAGS="-L$LIB -Wl,-rpath,$LIB" go run .
//
// Exit code 0 when every check below holds, 1 when one does not (each is printed).
package main

/*
#cgo LDFLAGS: -lcrc32c
#include "crc32c.h"
*/
import "C"

import (
	"fmt"
	"os"
	"unsafe"
)

// crc32c returns the CRC-32C of data continued from crc (0 starts a new checksum), on the
// fastest path this CPU has.
//
// The slice's memory is passed to the library in place, which cgo allows because it holds no Go
// pointers and the library keeps no pointer after it returns. An empty slice passes a nil
// pointer with length 0, which the library accepts.
func crc32c(crc uint32, data []byte) uint32 {
	return uint32(C.crc32c_append(C.uint32_t(crc), bytesPointer(data), C.size_t(len(data))))
}

// crc32cPortable returns the same checksum on the library's table path, whatever the CPU.
func crc32cPortable(crc uint32, data []byte) uint32 {
	return uint32(C.crc32c_append_portable(C.uint32_t(crc), bytesPointer(data), C.size_t(len(data))))
}

// bytesPointer returns the address of data's first byte as the header's const uint8_t*, or nil
// for an empty slice (indexing data[0] there would panic).
func bytesPointer(data []byte) *C.uint8_t {
	if len(data) == 0 {
		return nil
	}
	return (*C.uint8_t)(unsafe.Pointer(&data[0]))
}

// pathName returns the library's name for what crc32c_implementation() returns, or "unknown"
// for any other number (a newer library).
func pathName(path int32) string {
	switch path {
	case 1:
		return "portable (table)"
	case 2:
		return "hardware (CRC32 instruction)"
	case 3:
		return "hardware, 3-way (CRC32 + carry-less multiply)"
	}
	return "unknown"
}

// check prints one check and reports whether it held, so every failure is shown, not just the
// first.
func check(what string, got, want uint32) bool {
	mark := ""
	if got != want {
		mark = "  FAILED"
	}
	fmt.Printf("%-28s 0x%08x%s\n", what, got, mark)
	if got != want {
		fmt.Printf("%-28s 0x%08x expected\n", "", want)
	}
	return got == want
}

func main() {
	ok := true
	checkBytes := []byte("123456789")
	// The standard check value, then the same bytes in two calls.
	ok = check(`crc32c("123456789")`, crc32c(0, checkBytes), 0xE3069283) && ok
	ok = check("in two calls", crc32c(crc32c(0, checkBytes[:4]), checkBytes[4:]), 0xE3069283) && ok

	// 1 MiB on the path this CPU runs, against the portable table path: every path must agree.
	value := make([]byte, 1<<20)
	for i := range value {
		value[i] = byte(i * 31 % 251)
	}
	ok = check("1 MiB", crc32c(0, value), crc32cPortable(0, value)) && ok

	path := int32(C.crc32c_implementation())
	fmt.Printf("%-28s %d, %s\n", "path on this CPU", path, pathName(path))
	if !ok {
		os.Exit(1)
	}
}
