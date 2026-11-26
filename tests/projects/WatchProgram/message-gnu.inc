// message-gnu.inc - the line WatchProgram prints (ARM64, GNU syntax). WatchTests rewrites "first"
// to "second" in a copy while dotnet watch runs the program.
.macro MESSAGE_TEXT
    .asciz "watch-program: first"
.endm
