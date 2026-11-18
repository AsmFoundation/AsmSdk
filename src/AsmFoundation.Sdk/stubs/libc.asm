; =============================================================================
; libc.asm - the link-time stand-in for glibc (TargetFramework=libc).
;
; The SDK assembles this file and links it as a shared object with
; SONAME libc.so.6. A project linked against it gets:
;   - DT_NEEDED libc.so.6, so the dynamic loader loads the real glibc;
;   - PLT/GOT entries for every libc function it calls, and copy relocations
;     for the few data objects (stdin, stdout, stderr, environ).
; Nothing here ever runs: at run time every symbol resolves to glibc's own
; definition. The bodies are a bare "ret" only because a function symbol needs
; an address.
;
; Why a stub instead of the real libc.so.6: linking against glibc normally
; needs a Linux sysroot (libc.so.6, libc_nonshared.a, the linker script). The
; stub lets the bundled toolchain link libc programs on Windows and macOS too.
;
; Footguns:
;   - Symbols are unversioned. At run time an unversioned reference binds to
;     glibc's default version of the symbol (memcpy@@GLIBC_2.14, not the old
;     memmove-like memcpy@GLIBC_2.2.5), which is what a C program compiled
;     today would use.
;   - pthread_* functions are listed here because they live in libc.so.6 since
;     glibc 2.34. On older glibc they are in libpthread.so.0, which this stub
;     does not name: libc programs need glibc 2.34 or later (Ubuntu 22.04+,
;     Debian 12+, RHEL 9+).
;   - A data object's size must equal glibc's (all four below are pointers),
;     because a copy relocation copies exactly st_size bytes.
;   - A function missing from the list fails the link with "undefined symbol".
;     Add it with <AsmLibcSymbol Include="name" /> in the project; the SDK
;     passes the names in EXTRA_LIBC_SYMBOLS.
; =============================================================================

bits 64

; STUB_FUNCTIONS name, ... - one global function symbol per name.
; The $ prefix makes NASM read each name as a symbol even when it is also an
; instruction mnemonic: libc has wait (the x87 WAIT instruction) and syscall.
%macro STUB_FUNCTIONS 1-*
%rep %0
global $%1:function
$%1:
    ret
%rotate 1
%endrep
%endmacro

; STUB_POINTER name - one global 8-byte data object (a pointer in glibc).
%macro STUB_POINTER 1
global $%1:data 8
$%1:
    dq 0
%endmacro

section .text

; Process start and exit
STUB_FUNCTIONS __libc_start_main, __cxa_finalize, __cxa_atexit, atexit, exit, _exit, abort
STUB_FUNCTIONS __stack_chk_fail, __assert_fail, __errno_location, __sched_cpucount

; Memory
STUB_FUNCTIONS malloc, free, calloc, realloc, reallocarray, aligned_alloc, posix_memalign
STUB_FUNCTIONS memalign, valloc, malloc_usable_size

; Strings and memory blocks
STUB_FUNCTIONS memcpy, memmove, memset, memcmp, memchr, memrchr, mempcpy
STUB_FUNCTIONS strlen, strnlen, strcmp, strncmp, strcasecmp, strncasecmp, strcpy, strncpy
STUB_FUNCTIONS stpcpy, strcat, strncat, strchr, strrchr, strstr, strdup, strndup, strerror
STUB_FUNCTIONS strerror_r, strtok_r, strspn, strcspn, strtol, strtoul, strtoll, strtoull
STUB_FUNCTIONS strtod, strtof, atoi, atol, atoll

; Standard I/O
STUB_FUNCTIONS printf, fprintf, sprintf, snprintf, dprintf, vprintf, vfprintf, vsprintf
STUB_FUNCTIONS vsnprintf, vdprintf, puts, fputs, fputc, putc, putchar, fwrite, fread, fopen
STUB_FUNCTIONS fdopen, fclose, fflush, fgets, fgetc, getc, getchar, getline, perror, setvbuf
STUB_FUNCTIONS fileno, sscanf

; Environment, sorting, randomness
STUB_FUNCTIONS getenv, secure_getenv, setenv, unsetenv, qsort, bsearch, rand, srand, random
STUB_FUNCTIONS srandom, getrandom, getentropy

; Time
STUB_FUNCTIONS clock_gettime, clock_getres, clock_nanosleep, nanosleep, usleep, sleep
STUB_FUNCTIONS gettimeofday, time, localtime_r, gmtime_r, strftime

; Threads (in libc.so.6 since glibc 2.34)
STUB_FUNCTIONS pthread_create, pthread_join, pthread_detach, pthread_self, pthread_exit
STUB_FUNCTIONS pthread_equal, pthread_kill, pthread_sigmask, pthread_once, pthread_yield
STUB_FUNCTIONS pthread_setname_np, pthread_getname_np, pthread_attr_init, pthread_attr_destroy
STUB_FUNCTIONS pthread_attr_setstacksize, pthread_attr_setdetachstate
STUB_FUNCTIONS pthread_attr_setaffinity_np, pthread_setaffinity_np, pthread_getaffinity_np
STUB_FUNCTIONS pthread_mutex_init, pthread_mutex_destroy, pthread_mutex_lock
STUB_FUNCTIONS pthread_mutex_trylock, pthread_mutex_unlock, pthread_mutexattr_init
STUB_FUNCTIONS pthread_mutexattr_destroy, pthread_mutexattr_settype
STUB_FUNCTIONS pthread_cond_init, pthread_cond_destroy, pthread_cond_wait
STUB_FUNCTIONS pthread_cond_timedwait, pthread_cond_signal, pthread_cond_broadcast
STUB_FUNCTIONS pthread_rwlock_init, pthread_rwlock_destroy, pthread_rwlock_rdlock
STUB_FUNCTIONS pthread_rwlock_wrlock, pthread_rwlock_unlock, pthread_spin_init
STUB_FUNCTIONS pthread_spin_destroy, pthread_spin_lock, pthread_spin_trylock, pthread_spin_unlock
STUB_FUNCTIONS pthread_key_create, pthread_key_delete, pthread_getspecific, pthread_setspecific
STUB_FUNCTIONS pthread_barrier_init, pthread_barrier_destroy, pthread_barrier_wait

; Signals
STUB_FUNCTIONS signal, sigaction, sigemptyset, sigfillset, sigaddset, sigdelset, sigismember
STUB_FUNCTIONS sigprocmask, sigwaitinfo, sigtimedwait, sigwait, raise, kill

; Scheduling and system information
STUB_FUNCTIONS sched_yield, sched_getaffinity, sched_setaffinity, sched_getcpu, get_nprocs
STUB_FUNCTIONS get_nprocs_conf, sysconf, getpagesize, uname, prctl, syscall

; Files and descriptors
STUB_FUNCTIONS open, openat, close, read, write, pread, pwrite, readv, writev, lseek, fstat
STUB_FUNCTIONS stat, lstat, fstatat, ftruncate, fsync, fdatasync, unlink, unlinkat, mkdir
STUB_FUNCTIONS rmdir, rename, access, dup, dup2, dup3, pipe, pipe2, fcntl, ioctl, isatty
STUB_FUNCTIONS mmap, munmap, mprotect, madvise, mremap, msync, mlock, munlock, memfd_create

; Sockets and name resolution
STUB_FUNCTIONS socket, socketpair, bind, listen, accept, accept4, connect, shutdown
STUB_FUNCTIONS send, sendto, sendmsg, sendmmsg, recv, recvfrom, recvmsg, recvmmsg
STUB_FUNCTIONS setsockopt, getsockopt, getsockname, getpeername, getaddrinfo, freeaddrinfo
STUB_FUNCTIONS gai_strerror, inet_pton, inet_ntop, htons, ntohs, htonl, ntohl

; Event notification
STUB_FUNCTIONS epoll_create, epoll_create1, epoll_ctl, epoll_wait, epoll_pwait, poll, ppoll
STUB_FUNCTIONS select, pselect, eventfd, eventfd_read, eventfd_write, timerfd_create
STUB_FUNCTIONS timerfd_settime, timerfd_gettime, signalfd

; Processes and identity
STUB_FUNCTIONS getpid, getppid, gettid, fork, vfork, execve, execvp, waitpid, wait
STUB_FUNCTIONS getuid, geteuid, getgid, getegid, getrlimit, setrlimit, getrusage

; Dynamic loading (in libc.so.6 since glibc 2.34)
STUB_FUNCTIONS dlopen, dlsym, dlclose, dlerror, dladdr, dl_iterate_phdr

; Names the project added with AsmLibcSymbol items.
%ifdef EXTRA_LIBC_SYMBOLS
STUB_FUNCTIONS EXTRA_LIBC_SYMBOLS
%endif

section .data

STUB_POINTER stdin
STUB_POINTER stdout
STUB_POINTER stderr
STUB_POINTER environ

section .note.GNU-stack noalloc noexec nowrite progbits
