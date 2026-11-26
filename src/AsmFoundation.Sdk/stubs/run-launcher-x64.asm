; run-launcher-x64.asm - the launcher dotnet run starts, on Windows, for an asm program that runs
; through wasmtime, WSL or Docker while its project has launch settings (AsmSelectRunTarget).
;
; Why it exists: the .NET CLI applies a launch profile's commandLineArgs only to a run command
; that has no arguments of its own (RunCommand.cs, CommonRunHelpers.CombineRunArguments), and
; "wasmtime run ...", "wsl.exe --exec ..." and "docker run ..." always have some, so a profile's
; arguments never reached the program (the owner's choice for AsmSdk, 2026-11-26: build this
; launcher). dotnet run starts this program with no arguments of its own instead: the CLI then
; appends the profile's arguments (or those after "--"), and the launcher starts the real
; command line, which it reads from "<its own path>.run", with them appended.
;
; "<launcher>.run" (UTF-16, written by the SDK, a byte-order mark allowed):
;   line 1  the command line to start: the runtime, quoted, and its fixed arguments
;   line 2  for WSL only: the names of the variables a program may need ("A:B:C"); each one that
;           is set in this process's environment when it starts (by the launch profile, by -e or
;           by the user) is added to WSLENV as "NAME/u", the only way a Windows variable reaches
;           a Linux program. A name that is not set is not added: WSL would give the program an
;           empty variable for it. WSLENV's own entries stay first.
;
; What it passes on unchanged: its standard handles (STARTF_USESTDHANDLES, the handles it got),
; its environment (with WSLENV as above) and current directory, and the program's exit code.
; Ctrl+C and Ctrl+Break: handled by the launcher's own handler (ctrl_handler), which keeps it
; alive, while the program, which shares the console, gets them and decides; the launcher waits
; for its exit code. Ctrl+C processing is turned back on first, for a launcher started in a new
; process group (dotnet watch). The program runs in a kill-on-close job: killing the launcher
; kills it too, so nothing runs on orphaned when dotnet watch or an IDE stops the run.
; Failures say so on standard error and exit with 1: no launch file, a command line longer than
; Windows allows (32767 characters), a command that cannot start.
;
; Built by the SDK, when it is first needed, with the bundled NASM and lld-link, as a
; freestanding x64 program against kernel32 alone (Windows on Arm runs it under its x64
; emulation); never part of the user's own build.

default rel
bits 64

extern GetCommandLineW
extern GetModuleFileNameW
extern CreateFileW
extern ReadFile
extern CloseHandle
extern GetEnvironmentVariableW
extern SetEnvironmentVariableW
extern SetConsoleCtrlHandler
extern GetStdHandle
extern WriteFile
extern CreateProcessW
extern WaitForSingleObject
extern GetExitCodeProcess
extern ExitProcess
extern CreateJobObjectW
extern SetInformationJobObject
extern AssignProcessToJobObject
extern ResumeThread

; Wide-character buffers hold 32768 characters: Windows' longest command line and longest
; environment value are 32767, plus the NUL.
%define CHARS 32768
%define GENERIC_READ 0x80000000
%define FILE_SHARE_READ 1
%define OPEN_EXISTING 3
%define STD_INPUT_HANDLE -10
%define STD_OUTPUT_HANDLE -11
%define STD_ERROR_HANDLE -12
%define STARTF_USESTDHANDLES 0x100
%define INFINITE 0xFFFFFFFF
%define CREATE_SUSPENDED 4
%define JOB_OBJECT_EXTENDED_LIMIT_INFORMATION 9   ; JobObjectExtendedLimitInformation
%define JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 0x2000
%define JOB_LIMITS_SIZE 144                       ; JOBOBJECT_EXTENDED_LIMIT_INFORMATION on x64
%define JOB_LIMIT_FLAGS 16                        ; its BasicLimitInformation.LimitFlags

section .rdata
wslenv_name: dw __utf16__('WSLENV'), 0
run_suffix:  dw __utf16__('.run'), 0
msg_launch_file: db "asm-run: cannot read the launch file next to this launcher (<launcher>.run), which dotnet run writes", 13, 10
msg_launch_file_length equ $ - msg_launch_file
msg_too_long: db "asm-run: the command line is longer than Windows allows (32767 characters)", 13, 10
msg_too_long_length equ $ - msg_too_long
msg_start: db "asm-run: the program's command could not be started (CreateProcessW failed)", 13, 10
msg_start_length equ $ - msg_start

section .bss
alignb 16
path:    resw CHARS             ; this launcher's path, then the launch file's
config:  resw CHARS             ; the launch file's text
command: resw CHARS             ; the command line to start
wslenv:  resw CHARS             ; WSLENV, as it will be set
startup: resb 104               ; STARTUPINFOW
process: resb 24                ; PROCESS_INFORMATION
job_limits: resb JOB_LIMITS_SIZE    ; JOBOBJECT_EXTENDED_LIMIT_INFORMATION, zero but its flags
job:     resq 1                 ; the job the program runs in; 0 when there is none
count:   resd 1                 ; the byte count ReadFile and WriteFile store
exit:    resd 1                 ; the program's exit code

section .text
global asm_run_launcher

; The entry point (lld-link -entry). It never returns: every path ends in ExitProcess, so the
; registers that the Windows x64 convention asks a function to preserve are free here. Entered
; with rsp 8 bytes off a 16-byte boundary; the 88 bytes below make the calls aligned and hold
; the shadow space and CreateProcessW's six stack arguments (rsp+32 .. rsp+79).
asm_run_launcher:
    sub     rsp, 88

    ; Ctrl+C processing on, for this process and so for the program, which inherits the setting:
    ; a parent that starts the launcher in a process group of its own, to send Ctrl+C to it alone
    ; (dotnet watch does: ProcessRunner, CreateNewProcessGroup), gives it the "ignore Ctrl+C"
    ; flag with the group (CreateProcess's documentation), and the program would then ignore the
    ; Ctrl+C that is meant to stop it. A .NET app under dotnet watch turns it back on the same way
    ; (its startup hook).
    xor     ecx, ecx
    xor     edx, edx
    call    SetConsoleCtrlHandler

    ; Ctrl+C goes to the program, which shares the console; the launcher's handler says it is
    ; handled, so the launcher itself stays and waits for the program's exit code. A handler, not
    ; SetConsoleCtrlHandler(NULL, TRUE): that "ignore Ctrl+C" flag is inherited by child processes
    ; (the function's documentation), and the program then ignored Ctrl+C too (measured: ping -t
    ; started by the launcher ran on after Ctrl+C).
    lea     rcx, [ctrl_handler]
    mov     edx, 1
    call    SetConsoleCtrlHandler

    ; The launch file: this launcher's own path with ".run" appended.
    xor     ecx, ecx
    lea     rdx, [path]
    mov     r8d, CHARS - 8
    call    GetModuleFileNameW          ; the length in characters; 0, or the size given when cut off
    mov     eax, eax                    ; a DWORD result: the upper half of rax is undefined
    test    eax, eax
    jz      .no_launch_file
    cmp     eax, CHARS - 8
    jae     .no_launch_file
    ; A RIP-relative address takes no index register: the base first, then the index (written
    ; as [path + rax*2], NASM made it a 32-bit absolute address, which a 64-bit image loaded
    ; above 4 GiB truncates: a crash at the first write, measured).
    lea     rcx, [path]
    lea     rcx, [rcx + rax*2]
    lea     rdx, [run_suffix]
.append_suffix:
    mov     ax, [rdx]
    mov     [rcx], ax
    add     rcx, 2
    add     rdx, 2
    test    ax, ax
    jnz     .append_suffix

    lea     rcx, [path]
    mov     edx, GENERIC_READ
    mov     r8d, FILE_SHARE_READ
    xor     r9d, r9d                    ; no security attributes
    mov     qword [rsp + 32], OPEN_EXISTING
    mov     qword [rsp + 40], 0         ; no flags or attributes
    mov     qword [rsp + 48], 0         ; no template
    call    CreateFileW
    cmp     rax, -1
    je      .no_launch_file
    mov     rbx, rax                    ; the file
    mov     rcx, rbx
    lea     rdx, [config]
    mov     r8d, (CHARS - 1) * 2        ; room left for the NUL
    lea     r9, [count]
    mov     qword [rsp + 32], 0         ; not overlapped
    call    ReadFile
    mov     esi, eax                    ; whether it read
    mov     rcx, rbx
    call    CloseHandle
    test    esi, esi
    jz      .no_launch_file

    ; The text, NUL-terminated, past a byte-order mark if the writer put one.
    mov     eax, [count]
    shr     eax, 1                      ; bytes to characters
    lea     rsi, [config]
    mov     word [rsi + rax*2], 0
    cmp     word [rsi], 0xFEFF
    jne     .find_line_end
    add     rsi, 2

    ; Line 1 (the command line) is [rsi, r12); line 2 (the names) starts at r13.
.find_line_end:
    mov     r12, rsi
.scan_line:
    mov     ax, [r12]
    test    ax, ax
    jz      .line_end
    cmp     ax, 13
    je      .line_end
    cmp     ax, 10
    je      .line_end
    add     r12, 2
    jmp     .scan_line
.line_end:
    mov     r13, r12
.skip_line_break:
    mov     ax, [r13]
    cmp     ax, 13
    je      .skip_one
    cmp     ax, 10
    jne     .start_wslenv
.skip_one:
    add     r13, 2
    jmp     .skip_line_break

    ; WSLENV: the current value first (an unset or overlong one counts as empty).
.start_wslenv:
    lea     rcx, [wslenv_name]
    lea     rdx, [wslenv]
    mov     r8d, CHARS
    call    GetEnvironmentVariableW     ; the length; 0 when unset; more than CHARS when too long
    mov     eax, eax                    ; a DWORD result: the upper half of rax is undefined
    cmp     eax, CHARS
    jb      .wslenv_read
    xor     eax, eax
.wslenv_read:
    lea     r14, [wslenv]
    lea     r14, [r14 + rax*2]          ; where the next entry goes (base, then index: see above)
    mov     word [r14], 0
    xor     r15d, r15d                  ; the entries added

    ; Each name of line 2, separated by ':'.
.next_name:
    mov     ax, [r13]
    test    ax, ax
    jz      .names_done
    cmp     ax, 13
    je      .names_done
    cmp     ax, 10
    je      .names_done
    cmp     ax, ':'
    jne     .name_start
    add     r13, 2
    jmp     .next_name
.name_start:
    mov     rbp, r13                    ; the name is [rbp, r13)
.scan_name:
    mov     ax, [r13]
    test    ax, ax
    jz      .name_end
    cmp     ax, ':'
    je      .name_end
    cmp     ax, 13
    je      .name_end
    cmp     ax, 10
    je      .name_end
    add     r13, 2
    jmp     .scan_name
.name_end:
    ; Is it set? GetEnvironmentVariableW with no buffer returns the size it needs (its NUL
    ; included, so an empty value counts as set), and 0 only when the variable does not exist.
    ; The character after the name is set to NUL for the call, then put back.
    movzx   ebx, word [r13]
    mov     word [r13], 0
    mov     rcx, rbp
    xor     edx, edx
    xor     r8d, r8d
    call    GetEnvironmentVariableW
    mov     [r13], bx
    test    eax, eax
    jz      .next_name
    ; Room for ":" + the name + "/u" + the NUL (8 bytes beyond the name)? Else the name is left
    ; out: WSLENV cannot be longer than an environment value.
    mov     rax, r13
    sub     rax, rbp                    ; the name's bytes
    lea     rax, [r14 + rax + 8]
    lea     rcx, [wslenv + (CHARS - 1) * 2]
    cmp     rax, rcx
    ja      .next_name
    lea     rax, [wslenv]
    cmp     r14, rax
    je      .copy_name                  ; the first entry needs no separator
    mov     word [r14], ':'
    add     r14, 2
.copy_name:
    mov     rcx, rbp
.copy_name_character:
    cmp     rcx, r13
    jae     .name_copied
    mov     ax, [rcx]
    mov     [r14], ax
    add     rcx, 2
    add     r14, 2
    jmp     .copy_name_character
.name_copied:
    mov     dword [r14], 0x0075002F     ; "/u": from Windows into WSL
    add     r14, 4
    mov     word [r14], 0
    inc     r15d
    jmp     .next_name
.names_done:
    test    r15d, r15d
    jz      .build_command
    lea     rcx, [wslenv_name]
    lea     rdx, [wslenv]
    call    SetEnvironmentVariableW

    ; The command line: line 1, then this launcher's own arguments (its command line without
    ; argv[0], skipped by CommandLineToArgvW's rule for it: up to the closing quote when it
    ; starts with one, else up to the first space or tab), separated by one space.
.build_command:
    call    GetCommandLineW
    mov     rcx, rax
    cmp     word [rcx], '"'
    jne     .skip_unquoted
    add     rcx, 2
.skip_quoted:
    mov     ax, [rcx]
    test    ax, ax
    jz      .tail
    add     rcx, 2
    cmp     ax, '"'
    jne     .skip_quoted
    jmp     .skip_blanks
.skip_unquoted:
    mov     ax, [rcx]
    test    ax, ax
    jz      .tail
    cmp     ax, ' '
    je      .skip_blanks
    cmp     ax, 9
    je      .skip_blanks
    add     rcx, 2
    jmp     .skip_unquoted
.skip_blanks:
    mov     ax, [rcx]
    cmp     ax, ' '
    je      .skip_blank
    cmp     ax, 9
    jne     .tail
.skip_blank:
    add     rcx, 2
    jmp     .skip_blanks

.tail:
    lea     rdi, [command]
    lea     rdx, [command + (CHARS - 1) * 2]    ; the last character's place, kept for the NUL
    mov     r8, rsi
.copy_prefix:
    cmp     r8, r12
    jae     .prefix_copied
    cmp     rdi, rdx
    jae     .too_long
    mov     ax, [r8]
    mov     [rdi], ax
    add     r8, 2
    add     rdi, 2
    jmp     .copy_prefix
.prefix_copied:
    cmp     word [rcx], 0
    je      .command_built
    cmp     rdi, rdx
    jae     .too_long
    mov     word [rdi], ' '
    add     rdi, 2
.copy_tail:
    mov     ax, [rcx]
    test    ax, ax
    jz      .command_built
    cmp     rdi, rdx
    jae     .too_long
    mov     [rdi], ax
    add     rcx, 2
    add     rdi, 2
    jmp     .copy_tail
.command_built:
    mov     word [rdi], 0

    ; STARTUPINFOW: its size, and this launcher's standard handles for the program.
    mov     dword [startup], 104                        ; cb
    mov     dword [startup + 60], STARTF_USESTDHANDLES  ; dwFlags
    mov     ecx, STD_INPUT_HANDLE
    call    GetStdHandle
    mov     [startup + 80], rax                         ; hStdInput
    mov     ecx, STD_OUTPUT_HANDLE
    call    GetStdHandle
    mov     [startup + 88], rax                         ; hStdOutput
    mov     ecx, STD_ERROR_HANDLE
    call    GetStdHandle
    mov     [startup + 96], rax                         ; hStdError

    ; A job that ends the program with the launcher (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: the job
    ; closes with the launcher's last handle, when it exits or is killed). Whoever stops dotnet
    ; run's program by killing the process it started, the launcher, stops the program too:
    ; dotnet watch kills it after its Ctrl+C times out (Process.Kill, that process alone), an IDE
    ; when it stops a run. Without a job, wasmtime, wsl.exe or docker ran on, orphaned. Windows
    ; 8 and later nest jobs, so a launcher that runs in a job itself (a CI runner's) still makes
    ; one; when it cannot, the program runs without one, as it did before the launcher existed.
    xor     ecx, ecx
    xor     edx, edx
    call    CreateJobObjectW            ; no security attributes, no name
    test    rax, rax
    jz      .start_program
    mov     rbx, rax
    mov     dword [job_limits + JOB_LIMIT_FLAGS], JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    mov     rcx, rbx
    mov     edx, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION
    lea     r8, [job_limits]
    mov     r9d, JOB_LIMITS_SIZE
    call    SetInformationJobObject
    test    eax, eax
    jz      .start_program              ; a job without the limit would not help: none (the handle stays open, harmlessly)
    mov     [job], rbx

    ; CreateProcessW(NULL, command, NULL, NULL, TRUE, CREATE_SUSPENDED, NULL, NULL, &startup,
    ; &process): the handles inherited, this process's environment and current directory;
    ; suspended, so that it joins the job before it can start a process of its own (wsl.exe and
    ; docker do), which would otherwise be outside it.
.start_program:
    xor     ecx, ecx
    lea     rdx, [command]
    xor     r8d, r8d
    xor     r9d, r9d
    mov     qword [rsp + 32], 1                 ; bInheritHandles
    mov     qword [rsp + 40], CREATE_SUSPENDED  ; dwCreationFlags
    mov     qword [rsp + 48], 0                 ; lpEnvironment: this process's
    mov     qword [rsp + 56], 0                 ; lpCurrentDirectory: this process's
    lea     rax, [startup]
    mov     [rsp + 64], rax
    lea     rax, [process]
    mov     [rsp + 72], rax
    call    CreateProcessW
    test    eax, eax
    jz      .no_start

    ; Into the job (a failure leaves it outside, still running), then running.
    mov     rcx, [job]
    test    rcx, rcx
    jz      .resume
    mov     rdx, [process]              ; hProcess
    call    AssignProcessToJobObject
.resume:
    mov     rcx, [process + 8]          ; hThread
    call    ResumeThread

    mov     rcx, [process]              ; hProcess
    mov     edx, INFINITE
    call    WaitForSingleObject
    mov     rcx, [process]
    lea     rdx, [exit]
    call    GetExitCodeProcess
    mov     ecx, [exit]
    call    ExitProcess

.no_launch_file:
    lea     rbx, [msg_launch_file]
    mov     esi, msg_launch_file_length
    jmp     .fail
.too_long:
    lea     rbx, [msg_too_long]
    mov     esi, msg_too_long_length
    jmp     .fail
.no_start:
    lea     rbx, [msg_start]
    mov     esi, msg_start_length
.fail:
    mov     ecx, STD_ERROR_HANDLE
    call    GetStdHandle
    mov     rcx, rax
    mov     rdx, rbx
    mov     r8d, esi
    lea     r9, [count]
    mov     qword [rsp + 32], 0         ; not overlapped
    call    WriteFile
    mov     ecx, 1
    call    ExitProcess

; BOOL WINAPI ctrl_handler(DWORD type): the console's signal handler, which Windows calls on a
; thread of its own. TRUE (handled) for Ctrl+C (0) and Ctrl+Break (1), which the program gets too
; and acts on, so the launcher waits for it; FALSE for closing the console, logging off and
; shutting down (2, 5, 6), whose default handling ends the launcher as it ends the program. No
; calls and no stack: it needs nothing of the Windows x64 frame rules.
ctrl_handler:
    xor     eax, eax
    cmp     ecx, 1
    setbe   al
    ret
