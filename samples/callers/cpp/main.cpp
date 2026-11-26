// Crc32C from C++: the assembly library of samples/Crc32C, called through crc32c.h, the header
// AsmFoundation.Sdk writes from the library's PROTO lines at every build (next to the library, in
// bin/<Configuration>/freestanding/<rid>/). Nothing here is written by hand for the binding.
//
// Build and run on Linux (README.md has macOS and Windows):
//   dotnet build ../../Crc32C -c Release
//   LIB=../../Crc32C/bin/Release/freestanding/linux-x64
//   c++ -std=c++17 -Wall -Wextra -Werror -I"$LIB" main.cpp -L"$LIB" -lcrc32c -Wl,-rpath,"$LIB" -o crc32c-cpp
//   ./crc32c-cpp
//
// Exit code 0 when every check below holds, 1 when one does not (each is printed).

#include <cstdint>
#include <cstdio>
#include <string_view>
#include <vector>

#include "crc32c.h"  // extern "C" inside: the SDK's header guards its declarations for C++

namespace {

/// The library's name for what crc32c_implementation() returns.
/// @param path 1, 2 or 3, as crc32c_implementation() reports it.
/// @returns A short description, or "unknown" for any other number (a newer library).
const char* PathName(std::int32_t path) {
    switch (path) {
        case 1: return "portable (table)";
        case 2: return "hardware (CRC32 instruction)";
        case 3: return "hardware, 3-way (CRC32 + carry-less multiply)";
        default: return "unknown";
    }
}

/// Prints one check and counts it when it fails, so every failure is shown, not just the first.
/// @param what What was computed, as printed.
/// @param got The library's answer.
/// @param want The answer it must give.
/// @param failures Incremented when got differs from want.
void Check(const char* what, std::uint32_t got, std::uint32_t want, int& failures) {
    std::printf("%-28s 0x%08x%s\n", what, static_cast<unsigned>(got), got == want ? "" : "  FAILED");
    if (got != want) {
        std::printf("%-28s 0x%08x expected\n", "", static_cast<unsigned>(want));
        ++failures;
    }
}

}  // namespace

int main() {
    int failures = 0;
    constexpr std::string_view kCheck = "123456789";
    const auto* bytes = reinterpret_cast<const std::uint8_t*>(kCheck.data());

    // The standard check value, then the same bytes in two calls: crc 0 starts a checksum, and a
    // previous result continues one.
    Check("crc32c(\"123456789\")", crc32c_append(0, bytes, kCheck.size()), 0xE3069283u, failures);
    Check("in two calls", crc32c_append(crc32c_append(0, bytes, 4), bytes + 4, kCheck.size() - 4),
          0xE3069283u, failures);

    // 1 MiB on the path this CPU runs, against the portable table path: every path must agree.
    std::vector<std::uint8_t> value(1u << 20);
    for (std::size_t i = 0; i < value.size(); ++i) {
        value[i] = static_cast<std::uint8_t>(i * 31 % 251);
    }
    Check("1 MiB", crc32c_append(0, value.data(), value.size()),
          crc32c_append_portable(0, value.data(), value.size()), failures);

    const std::int32_t path = crc32c_implementation();
    std::printf("%-28s %d, %s\n", "path on this CPU", static_cast<int>(path), PathName(path));
    return failures == 0 ? 0 : 1;
}
