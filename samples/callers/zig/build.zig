//! Builds the Zig caller of samples/Crc32C's assembly library.
//!
//! -Dcrc32c=<folder> names the folder of the library's build for the target, for example
//! ../../Crc32C/bin/Release/freestanding/linux-x64. That folder holds both the library and
//! crc32c.h, the header AsmFoundation.Sdk writes from the library's PROTO lines. Zig translates the
//! header into a module (translate-c), so the declarations in src/main.zig come from the header,
//! not from code written by hand.
//!
//!   zig build run -Dcrc32c=$(realpath ../../Crc32C/bin/Release/freestanding/linux-x64)

const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    // No default: a wrong folder would fail later with a less helpful message. Without the option,
    // building fails with this message, but `zig build -h` (which runs this function too) still
    // lists the option.
    const folder = b.option([]const u8, "crc32c", "the folder of samples/Crc32C's build for the target") orelse {
        const missing = b.addFail("pass -Dcrc32c=<folder of samples/Crc32C's build for the target>");
        b.getInstallStep().dependOn(&missing.step);
        b.step("run", "Build the caller and run its checks").dependOn(&missing.step);
        return;
    };

    // crc32c.h as a Zig module. It includes stdint.h and stddef.h, so translate-c needs libc's
    // headers, which Zig carries for every target.
    const header = b.addTranslateC(.{
        .root_source_file = .{ .cwd_relative = b.pathJoin(&.{ folder, "crc32c.h" }) },
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });

    const module = b.createModule(.{
        .root_source_file = b.path("src/main.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{.{ .name = "crc32c", .module = header.createModule() }},
    });
    module.addLibraryPath(.{ .cwd_relative = folder });
    module.linkSystemLibrary("crc32c", .{});
    // Linux and macOS: the program finds the library in its build folder at run time, without
    // LD_LIBRARY_PATH or DYLD_LIBRARY_PATH.
    if (target.result.os.tag != .windows) module.addRPath(.{ .cwd_relative = folder });

    const exe = b.addExecutable(.{ .name = "crc32c-caller", .root_module = module });
    b.installArtifact(exe);

    const run = b.addRunArtifact(exe);
    b.step("run", "Build the caller and run its checks").dependOn(&run.step);
}
