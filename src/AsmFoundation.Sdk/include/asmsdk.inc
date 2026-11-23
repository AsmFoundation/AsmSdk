; =============================================================================
; asmsdk.inc - AsmFoundation.Sdk's calling-convention layer for NASM (x86-64).
;
; One x86-64 source builds for Windows, Linux and macOS, but the three do not
; call functions the same way:
;
;                     Windows (Microsoft x64)     Linux, macOS (System V)
;   integer args 1-4  rcx, rdx, r8, r9            rdi, rsi, rdx, rcx
;   integer args 5-6  on the stack                r8, r9
;   callee-saved      rbx rbp rdi rsi r12-r15     rbx rbp r12-r15
;                     xmm6-xmm15
;   shadow space      32 bytes above the return   none
;                     address, for the callee
;   exports           only what the DLL lists     every global symbol
;
; This file gives each difference a name, so the code says what it means and
; the SDK picks the registers per OS at assembly time. It adds no instruction
; the hand-written code for that OS would not have.
;
;   %include "asmsdk.inc"            ; the SDK puts it on every include path
;
;   FUNCTION crc32c_append, 3, rbx   ; exported; 3 integer arguments; uses rbx
;       mov     eax, ARG1d           ; edi on Linux and macOS, ecx on Windows
;       mov     rbx, ARG2            ; rsi / rdx
;       ...
;       RETURN                       ; restores rbx, then ret
;   ENDFUNCTION
;
; FUNCTION name [, nargs [, register ...]]
;   Starts a function and exports it on every OS: "global name:function" for
;   ELF, "global" for Mach-O (NASM adds the underscore; the SDK passes
;   --gprefix _), and on Windows also an "-export:name" linker directive in a
;   .drectve section, which is what __declspec(dllexport) does in C.
;   A project with an ExportsFile gets ASMSDK_EXPORTS_FILE defined, and then
;   FUNCTION writes no directive: the file decides what is exported, on
;   Windows as the version script does on Linux. An executable gets
;   ASMSDK_EXECUTABLE, and no directive either: a program exports nothing
;   unless its ExportsFile says so, as a C program's main is no export.
;   nargs (0-6) is the number of integer arguments: on Windows, arguments 5
;   and 6 are loaded from the stack into ARG5 (r10) and ARG6 (r11) at entry.
;   The registers listed are the ones the body changes; FUNCTION saves those
;   that the caller expects back ON THIS OS (rbx rbp r12-r15 everywhere; rdi,
;   rsi and xmm6-xmm15 on Windows only), so listing rsi costs nothing on
;   Linux and keeps Windows correct.
; LOCAL_FUNCTION name [, nargs [, register ...]]
;   The same, for a function that is not exported (not global).
; HIDDEN_FUNCTION name [, nargs [, register ...]]
;   The same, for a function the library's other files (or a static
;   library's users) call, but that no image exports: global, with ELF's
;   hidden visibility on Linux and Mach-O's private_extern on macOS, and no
;   export directive on Windows. Use it for the internals of a static
;   library, whose FUNCTIONs every shared library that links it exports.
; RETURN
;   Restores what FUNCTION saved and returns. Use it instead of "ret"; it may
;   appear more than once in a function.
; ENDFUNCTION
;   Closes the function started by FUNCTION or LOCAL_FUNCTION.
;
; ARG1 .. ARG6       the integer argument registers, by position, with the
;                    32-, 16- and 8-bit forms ARG1d, ARG1w, ARG1b and so on.
;                    The return value is rax (eax, ...) everywhere.
; SHADOW_SPACE       bytes a caller must reserve above the return address
;                    before calling another function: 32 on Windows, 0 else.
; PLT                "wrt ..plt" on Linux, where calls to another module go
;                    through the PLT, and nothing elsewhere:
;                        call    printf PLT
;
; The SDK also defines WINDOWS, LINUX or MACOS (and MUSL), and X64 or ARM64,
; for what truly differs between platforms (system calls, C library names).
; Assembled outside AsmSdk, the OS is taken from the object format instead.
;
; Footguns:
;   - Stack alignment is still yours: rsp is 16n+8 at entry, and each saved
;     register moves it by 8. A function that calls another must align rsp
;     to 16 and, on Windows, reserve SHADOW_SPACE below its own data.
;   - Floating-point arguments are positional on Windows (xmm0-xmm3 with the
;     integer ones) but sequential on System V (xmm0-xmm7); these macros name
;     integer arguments only.
;   - On Windows, a function with more than 4 arguments must say how many,
;     or ARG5 and ARG6 hold garbage.
;   - "section .rodata" works on every OS: macho64 maps it to __TEXT,__const,
;     and the SDK defines .rodata as .rdata for win64, whose NASM output would
;     otherwise make an unknown section executable code (and crash with
;     CodeView debug information). Assembled outside AsmSdk for Windows,
;     write .rdata, or pass -D.rodata=.rdata yourself.
;   - A static library's FUNCTIONs are exported again by every shared library
;     that links it: through their directives on Windows (even when the DLL's
;     own ExportsFile does not list them: lld-link has no switch that drops a
;     directive), as global symbols on Linux and macOS (unless an ExportsFile
;     hides them). Keep a static library's FUNCTIONs to its public API: use
;     LOCAL_FUNCTION for what is private to one file, and HIDDEN_FUNCTION for
;     what its other files or its users call.
;   - Each function starts with NASM's __SECT__, the section the source last
;     named with a "section" line, or "[section .text]" while it named none.
;     NASM's COFF output (Windows) writes a label defined before the first
;     section directive as an undefined symbol, so a source with no section
;     line could not be linked there; the SDK's ImplicitUsings names .text
;     too, and this covers a source assembled without it. The one change of
;     meaning: after the bracket form "[section .foo]", which does not
;     redefine __SECT__, a function goes back to the last "section" line's
;     section. Use the "section .foo" form for code.
; =============================================================================

%ifndef ASMSDK_INC
%define ASMSDK_INC

; ---- The OS: from the SDK's defines, or else from the object format --------
%ifndef WINDOWS
  %ifndef LINUX
    %ifndef MACOS
      %ifidn __OUTPUT_FORMAT__, win64
        %define WINDOWS
      %elifidn __OUTPUT_FORMAT__, macho64
        %define MACOS
      %else
        %define LINUX
      %endif
    %endif
  %endif
%endif

; ---- Integer argument registers --------------------------------------------
%ifdef WINDOWS
  %define ARG1  rcx
  %define ARG1d ecx
  %define ARG1w cx
  %define ARG1b cl
  %define ARG2  rdx
  %define ARG2d edx
  %define ARG2w dx
  %define ARG2b dl
  %define ARG3  r8
  %define ARG3d r8d
  %define ARG3w r8w
  %define ARG3b r8b
  %define ARG4  r9
  %define ARG4d r9d
  %define ARG4w r9w
  %define ARG4b r9b
  ; Arguments 5 and 6 arrive on the stack; FUNCTION loads them here.
  %define ARG5  r10
  %define ARG5d r10d
  %define ARG5w r10w
  %define ARG5b r10b
  %define ARG6  r11
  %define ARG6d r11d
  %define ARG6w r11w
  %define ARG6b r11b
  %define SHADOW_SPACE 32
%else
  %define ARG1  rdi
  %define ARG1d edi
  %define ARG1w di
  %define ARG1b dil
  %define ARG2  rsi
  %define ARG2d esi
  %define ARG2w si
  %define ARG2b sil
  %define ARG3  rdx
  %define ARG3d edx
  %define ARG3w dx
  %define ARG3b dl
  %define ARG4  rcx
  %define ARG4d ecx
  %define ARG4w cx
  %define ARG4b cl
  %define ARG5  r8
  %define ARG5d r8d
  %define ARG5w r8w
  %define ARG5b r8b
  %define ARG6  r9
  %define ARG6d r9d
  %define ARG6w r9w
  %define ARG6b r9b
  %define SHADOW_SPACE 0
%endif

%ifdef LINUX
  %define PLT wrt ..plt
%else
  %define PLT
%endif

; ---- Functions ---------------------------------------------------------------

; ASMSDK_FUNCTION_BEGIN scope, name [, nargs [, register ...]]
; The body of FUNCTION, LOCAL_FUNCTION and HIDDEN_FUNCTION (internal: use those).
; scope: 1 exported (FUNCTION), 0 file-local (LOCAL_FUNCTION), 2 global but
; exported from no image (HIDDEN_FUNCTION).
%macro ASMSDK_FUNCTION_BEGIN 2-*
    %push asmsdk_function
    %xdefine %$gprs                  ; saved general registers, in push order
    %xdefine %$xmms                  ; saved xmm registers, in save order
    %assign %$gpr_count 0
    %assign %$xmm_count 0
    %if %0 >= 3
        %assign %$nargs %3
    %else
        %assign %$nargs 0
    %endif

    %if %1 == 2
        ; Global within the image, exported from none: no .drectve directive on
        ; Windows (a DLL exports nothing it is not told to), private_extern on
        ; Mach-O and hidden visibility on ELF, which the linker turns into a
        ; local symbol of the output. A call to it from another object is a
        ; direct call, through no PLT or stub.
        %ifdef WINDOWS
            global %2
        %elifdef MACOS
            global %2:private_extern
        %else
            global %2:function hidden
        %endif
    %elif %1 == 1
        %ifdef WINDOWS
            global %2
            %ifndef ASMSDK_EXPORTS_FILE
              %ifndef ASMSDK_EXECUTABLE
                ; dllexport: a linker directive in .drectve, which lld-link
                ; reads and which never reaches the image ("info"). Left out
                ; when the project's ExportsFile names the exports instead: a
                ; directive would add this function to them, listed or not.
                ; Left out of programs too, whose main would otherwise be an
                ; export and get the .exe an import library.
                %defstr %$export -export:%2
                [section .drectve info]
                    db " ", %$export
                __SECT__
              %endif
            %endif
        %elifdef MACOS
            global %2
        %else
            global %2:function
        %endif
    %endif
    ; Back to the section the source named last ("[section .text]" while it
    ; named none): NASM's COFF output defines a label only in a section some
    ; directive named, and writes one in its implicit default section as an
    ; undefined symbol (measured with NASM 2.16.03, 3.01 and 3.02). The export
    ; directive above switches back the same way, so a FUNCTION of a DLL was
    ; already covered; a HIDDEN_FUNCTION, a LOCAL_FUNCTION and a program's or
    ; an ExportsFile project's FUNCTION were not, without ImplicitUsings.
    __SECT__
%2:
    %ifdef WINDOWS
        ; Arguments 5 and 6: above the return address and the 32-byte shadow
        ; space, before anything is pushed.
        %if %$nargs >= 5
            mov     r10, [rsp + 40]
        %endif
        %if %$nargs >= 6
            mov     r11, [rsp + 48]
        %endif
    %endif

    %if %0 > 3
        %rotate 3
        %rep %0 - 3
            ASMSDK_SAVE %1
            %rotate 1
        %endrep
    %endif
    %if %$xmm_count > 0
        sub     rsp, 16 * %$xmm_count
        ASMSDK_STORE_XMMS %$xmms
    %endif
%endmacro

; ASMSDK_SAVE register - pushes a general register, or queues an xmm register
; to store, when the current OS's caller expects it back; else nothing.
%macro ASMSDK_SAVE 1
    %assign %%gpr 0
    %assign %%xmm 0
    %ifidni %1, rbx
        %assign %%gpr 1
    %elifidni %1, rbp
        %assign %%gpr 1
    %elifidni %1, r12
        %assign %%gpr 1
    %elifidni %1, r13
        %assign %%gpr 1
    %elifidni %1, r14
        %assign %%gpr 1
    %elifidni %1, r15
        %assign %%gpr 1
    %endif
    %ifdef WINDOWS
        %ifidni %1, rdi
            %assign %%gpr 1
        %elifidni %1, rsi
            %assign %%gpr 1
        %elifidni %1, xmm6
            %assign %%xmm 1
        %elifidni %1, xmm7
            %assign %%xmm 1
        %elifidni %1, xmm8
            %assign %%xmm 1
        %elifidni %1, xmm9
            %assign %%xmm 1
        %elifidni %1, xmm10
            %assign %%xmm 1
        %elifidni %1, xmm11
            %assign %%xmm 1
        %elifidni %1, xmm12
            %assign %%xmm 1
        %elifidni %1, xmm13
            %assign %%xmm 1
        %elifidni %1, xmm14
            %assign %%xmm 1
        %elifidni %1, xmm15
            %assign %%xmm 1
        %endif
    %endif
    %if %%gpr
        push    %1
        %if %$gpr_count
            %xdefine %$gprs %$gprs, %1
        %else
            %xdefine %$gprs %1
        %endif
        %assign %$gpr_count %$gpr_count + 1
    %elif %%xmm
        %if %$xmm_count
            %xdefine %$xmms %$xmms, %1
        %else
            %xdefine %$xmms %1
        %endif
        %assign %$xmm_count %$xmm_count + 1
    %endif
%endmacro

; ASMSDK_STORE_XMMS xmm ... - stores the queued xmm registers below the pushes.
; movdqu: the slots are not 16-byte aligned in general (it depends on how many
; registers were pushed), and an unaligned store costs nothing extra there.
%macro ASMSDK_STORE_XMMS 1-*
    %assign %%i 0
    %rep %0
        movdqu  [rsp + 16 * %%i], %1
        %assign %%i %%i + 1
        %rotate 1
    %endrep
%endmacro

; ASMSDK_LOAD_XMMS xmm ... - the reverse of ASMSDK_STORE_XMMS.
%macro ASMSDK_LOAD_XMMS 1-*
    %assign %%i 0
    %rep %0
        movdqu  %1, [rsp + 16 * %%i]
        %assign %%i %%i + 1
        %rotate 1
    %endrep
%endmacro

; ASMSDK_POP_REVERSE register ... - pops the registers in reverse order.
%macro ASMSDK_POP_REVERSE 1-*
    %rep %0
        %rotate -1
        pop     %1
    %endrep
%endmacro

%macro FUNCTION 1-*
    ASMSDK_FUNCTION_BEGIN 1, %{1:-1}
%endmacro

%macro LOCAL_FUNCTION 1-*
    ASMSDK_FUNCTION_BEGIN 0, %{1:-1}
%endmacro

%macro HIDDEN_FUNCTION 1-*
    ASMSDK_FUNCTION_BEGIN 2, %{1:-1}
%endmacro

%macro RETURN 0
    %ifnctx asmsdk_function
        %error "RETURN outside a FUNCTION ... ENDFUNCTION block"
    %endif
    %if %$xmm_count > 0
        ASMSDK_LOAD_XMMS %$xmms
        add     rsp, 16 * %$xmm_count
    %endif
    %if %$gpr_count > 0
        ASMSDK_POP_REVERSE %$gprs
    %endif
    ret
%endmacro

%macro ENDFUNCTION 0
    %ifnctx asmsdk_function
        %error "ENDFUNCTION without FUNCTION"
    %endif
    %pop
%endmacro

%endif ; ASMSDK_INC
