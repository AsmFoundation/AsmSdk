// ctrlc.cs - does stopping dotnet run's Windows launcher stop the program it started?
// (WebAssemblyTests.Stopping_the_launcher_stops_the_program, Windows only.)
//
// A file-based app, a process of its own because it joins another console, which a test host
// shared by parallel tests must not do:
//
//   dotnet run ctrlc.cs -- <mode> <launcher.exe>
//
// It points the launcher's launch file at "ping -t 127.0.0.1" (it runs until Ctrl+C), starts the
// launcher in a hidden console, waits until ping runs under it, then stops the run as
//   ctrl-c        a user does: the launcher in a console of its own, Ctrl+C to every process on it;
//   group-ctrl-c  dotnet watch does (ProcessRunner): the launcher on the console of the process
//                 that starts it, in a new process group (Windows then sets its inherited "ignore
//                 Ctrl+C" flag), and Ctrl+C to that group alone. The probe makes a hidden console
//                 with a holder process (cmd /k), joins it and starts the launcher there:
//                 CREATE_NEW_PROCESS_GROUP is ignored together with CREATE_NEW_CONSOLE (the
//                 CreateProcess documentation), so the launcher cannot own that console;
//   kill          dotnet watch does after its Ctrl+C times out, and an IDE when it stops a run:
//                 the launcher killed (TerminateProcess), the program left to the launcher's job.
// Exit code 0 when ping ended (with Ctrl+C, the launcher passes on its 0xC000013A,
// STATUS_CONTROL_C_EXIT, printed as "exited 0xC000013A"); 1 when ping still ran (then killed);
// 2 when the probe itself could not run.
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

if (args.Length != 2 || !OperatingSystem.IsWindows() || args[0] is not ("ctrl-c" or "group-ctrl-c" or "kill"))
{
    Console.WriteLine("usage (Windows): dotnet run ctrlc.cs -- ctrl-c|group-ctrl-c|kill <launcher.exe>");
    return 2;
}

string mode = args[0];
string launcher = Path.GetFullPath(args[1]);
// Console.Out made now, while the standard output is still the test's pipe: Console.Out keeps the
// handle it was made with. The probe leaves its console and joins another (FreeConsole,
// AttachConsole), and when the .NET 10 SDK's dotnet run started it, AttachConsole replaced the
// standard output with a handle of the joined console, dead once the probe left that console
// again (measured with 10.0.404, runtime 10.0.15: a pipe, then a console handle, then an invalid
// one; with 11.0.100 the pipe stayed). A Console.Out first made after that wrote nowhere, so the
// probe ended with 0 and the test read no "exited" line (the win-x64 runner, and here).
Console.WriteLine($"ctrlc probe: {mode}, launcher {launcher}");
string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
// The launch file as AsmSelectRunTarget writes it: UTF-16 with a byte-order mark, line 1 the
// command line, line 2 the WSL names (none here).
File.WriteAllText(launcher + ".run", "\"" + ping + "\" -t 127.0.0.1\r\n\r\n", Encoding.Unicode);

// ctrl-c and kill: the launcher in a new console (hidden: STARTF_USESHOWWINDOW with SW_HIDE).
// group-ctrl-c: a hidden console of the holder's, which this process joins; the launcher then
// starts on it in a new process group (its id is the launcher's process id), with the console's
// own input and output as its standard handles (this process's are the test's pipes).
var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = Native.StartfUseShowWindow, ShowWindow = 0 };
Native.ProcessInformation holder = default;
uint creation = Native.CreateNewConsole;
bool inherit = false;
if (mode == "group-ctrl-c")
{
    string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    if (!Native.CreateProcessW(cmd, new StringBuilder("\"" + cmd + "\" /d /k"), IntPtr.Zero, IntPtr.Zero, false, Native.CreateNewConsole, IntPtr.Zero, null, ref startup, out holder))
    {
        Console.WriteLine($"CreateProcessW of the console holder failed: error {Marshal.GetLastPInvokeError()}");
        return 2;
    }

    // Its console exists once cmd runs: retry the join for a few seconds.
    Native.FreeConsole();
    var joining = Stopwatch.StartNew();
    while (!Native.AttachConsole((uint)holder.ProcessId))
    {
        if (joining.Elapsed > TimeSpan.FromSeconds(10))
        {
            Native.TerminateProcess(holder.Process, 1);
            Console.WriteLine($"AttachConsole to the holder failed: error {Marshal.GetLastPInvokeError()}");
            return 2;
        }

        Thread.Sleep(100);
    }

    IntPtr input = Native.OpenConsole("CONIN$");
    IntPtr output = Native.OpenConsole("CONOUT$");
    startup = new Native.StartupInfo
    {
        Size = Marshal.SizeOf<Native.StartupInfo>(),
        Flags = Native.StartfUseStdHandles,
        StdInput = input,
        StdOutput = output,
        StdError = output,
    };
    creation = Native.CreateNewProcessGroup;
    inherit = true;
}

if (!Native.CreateProcessW(launcher, new StringBuilder("\"" + launcher + "\""), IntPtr.Zero, IntPtr.Zero, inherit, creation, IntPtr.Zero, null, ref startup, out Native.ProcessInformation started))
{
    Console.WriteLine($"CreateProcessW failed: error {Marshal.GetLastPInvokeError()}");
    StopHolder(holder);
    return 2;
}

// A Process for the launcher, to kill its tree when the probe gives up (CreateProcessW's own
// handle is what the probe waits on and reads the exit code from).
Process launcherProcess;
try
{
    launcherProcess = Process.GetProcessById(started.ProcessId);
}
catch (ArgumentException)
{
    Native.GetExitCodeProcess(started.Process, out uint early);
    Console.WriteLine($"the launcher exited at once, code 0x{early:X8}");
    return 2;
}

// Stopping the run before ping runs would test nothing: wait for ping, under this launcher.
Process? program = null;
var deadline = Stopwatch.StartNew();
while ((program = PingUnder(started.ProcessId)) is null)
{
    if (Native.WaitForSingleObject(started.Process, 0) == 0 || deadline.Elapsed > TimeSpan.FromSeconds(20))
    {
        Console.WriteLine("ping did not start under the launcher within 20 s");
        Stop(launcherProcess);
        return 2;
    }

    Thread.Sleep(100);
}

using (program)
{
    if (mode == "kill")
    {
        // TerminateProcess only starts the termination: wait until the launcher is gone, so the
        // probe's caller can delete its file once the probe returns.
        Native.TerminateProcess(started.Process, 1);
        Native.WaitForSingleObject(started.Process, 10000);
    }
    else if (mode == "group-ctrl-c")
    {
        // Already on the holder's console, as dotnet watch is on its own: Ctrl+C to the
        // launcher's group alone, which neither this process nor the holder is in, once ping
        // runs (its first line is on the console).
        if (!PingWrote())
        {
            Stop(launcherProcess);
            Stop(program);
            StopHolder(holder);
            Console.WriteLine("ping wrote nothing to the console within 20 s");
            return 2;
        }

        bool sent = Native.GenerateConsoleCtrlEvent(0, (uint)started.ProcessId);
        if (!sent)
        {
            int error = Marshal.GetLastPInvokeError();
            Stop(launcherProcess);
            Stop(program);
            StopHolder(holder);
            Console.WriteLine($"GenerateConsoleCtrlEvent failed: error {error}");
            return 2;
        }
    }
    else
    {
        // Leave this process's console (if any), join the launcher's, and ignore Ctrl+C here:
        // ctrl-c sends the event to every process on that console, this one included.
        Native.FreeConsole();
        if (!Native.AttachConsole((uint)started.ProcessId))
        {
            int error = Marshal.GetLastPInvokeError();
            Stop(launcherProcess);
            Stop(program);
            Console.WriteLine($"AttachConsole failed: error {error}");
            return 2;
        }

        // Once ping runs (its first line is on the console): found in the process list, it may
        // still be suspended (the launcher starts it so, to put it in its job) and miss the event.
        if (!PingWrote())
        {
            Native.FreeConsole();
            Stop(launcherProcess);
            Stop(program);
            Console.WriteLine("ping wrote nothing to the console within 20 s");
            return 2;
        }

        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
        bool sent = Native.GenerateConsoleCtrlEvent(0, 0);
        Native.FreeConsole();
        if (!sent)
        {
            Stop(launcherProcess);
            Stop(program);
            Console.WriteLine("GenerateConsoleCtrlEvent failed");
            return 2;
        }
    }

    // The program must end: by Ctrl+C itself, or with the launcher's job when it was killed.
    if (!program.WaitForExit(15000))
    {
        Stop(launcherProcess);
        Stop(program);
        StopHolder(holder);
        Console.WriteLine($"ping still ran 15 s after {mode}: the run was not stopped");
        return 1;
    }

    if (mode != "kill")
    {
        if (Native.WaitForSingleObject(started.Process, 15000) != 0)
        {
            Stop(launcherProcess);
            StopHolder(holder);
            Console.WriteLine("ping ended, but the launcher still ran 15 s later");
            return 1;
        }

        Native.GetExitCodeProcess(started.Process, out uint code);
        StopHolder(holder);
        Console.WriteLine($"exited 0x{code:X8}");
        return code == 0xC000013A ? 0 : 1;
    }

    Console.WriteLine("ping ended with the killed launcher");
    return 0;
}

// Whether ping's first line ("Pinging 127.0.0.1 ...") is on the console this process is attached
// to, read from the screen buffer: then ping runs and handles Ctrl+C. Waits up to 20 s.
static bool PingWrote()
{
    IntPtr output = Native.OpenConsole("CONOUT$");
    var screen = new char[8000];
    var waiting = Stopwatch.StartNew();
    while (waiting.Elapsed < TimeSpan.FromSeconds(20))
    {
        if (Native.ReadConsoleOutputCharacterW(output, screen, (uint)screen.Length, 0, out uint read)
            && new string(screen, 0, (int)read).Contains("127.0.0.1", StringComparison.Ordinal))
        {
            Native.CloseHandle(output);
            return true;
        }

        Thread.Sleep(100);
    }

    Native.CloseHandle(output);
    return false;
}

// Ends the group-ctrl-c mode's console holder (cmd /k) and leaves its console: the holder keeps
// the hidden console only while the probe needs one. Nothing for the other modes (no holder).
static void StopHolder(Native.ProcessInformation holder)
{
    if (holder.Process != IntPtr.Zero)
    {
        Native.FreeConsole();
        Native.TerminateProcess(holder.Process, 0);
    }
}

// The PING.EXE whose parent is the launcher: this probe's ping, never one of the user's.
static Process? PingUnder(int launcherId)
{
    foreach (Process candidate in Process.GetProcessesByName("PING"))
    {
        try
        {
            var information = new Native.ProcessBasicInformation();
            if (Native.NtQueryInformationProcess(candidate.Handle, 0, ref information, Marshal.SizeOf<Native.ProcessBasicInformation>(), out _) == 0
                && information.InheritedFromUniqueProcessId == launcherId)
            {
                return candidate;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It exited, or belongs to a process this one may not open: not this probe's.
        }

        candidate.Dispose();
    }

    return null;
}

// Kills a process and what it started, when the probe ends with it still running.
static void Stop(Process process)
{
    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
        // It exited meanwhile: nothing left to stop.
    }
}

/// <summary>The Windows calls the probe needs (kernel32's process and console functions, ntdll's parent query).</summary>
static class Native
{
    /// <summary>CREATE_NEW_CONSOLE: the launcher gets a console of its own, which the probe can join and signal.</summary>
    public const uint CreateNewConsole = 0x10;

    /// <summary>CREATE_NEW_PROCESS_GROUP: as dotnet watch starts an app; Windows then sets the process's "ignore Ctrl+C" flag.</summary>
    public const uint CreateNewProcessGroup = 0x200;

    /// <summary>STARTF_USESHOWWINDOW: StartupInfo.ShowWindow counts (0, SW_HIDE: the console is not shown).</summary>
    public const int StartfUseShowWindow = 1;

    /// <summary>STARTF_USESTDHANDLES: StartupInfo's standard handles are the process's (they must be inheritable).</summary>
    public const int StartfUseStdHandles = 0x100;

    /// <summary>
    /// An inheritable handle to this process's console input ("CONIN$") or output ("CONOUT$"),
    /// for a child on the same console whose parent's own standard handles are pipes.
    /// </summary>
    /// <param name="name">"CONIN$" or "CONOUT$".</param>
    /// <returns>The handle; INVALID_HANDLE_VALUE (-1) when this process has no console.</returns>
    public static IntPtr OpenConsole(string name)
    {
        var inheritable = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        // GENERIC_READ | GENERIC_WRITE, shared for reading and writing, OPEN_EXISTING.
        return CreateFileW(name, 0xC0000000, 3, ref inheritable, 3, 0, IntPtr.Zero);
    }

    /// <summary>Reads characters from a console screen buffer.</summary>
    /// <param name="output">The buffer ("CONOUT$").</param>
    /// <param name="characters">Receives the characters, row after row.</param>
    /// <param name="length">How many to read.</param>
    /// <param name="coordinates">Where to start: a COORD (X in the low 16 bits, Y in the high), 0 for the top left.</param>
    /// <param name="read">Receives how many were read.</param>
    /// <returns>Whether it read.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ReadConsoleOutputCharacterW(IntPtr output, [Out] char[] characters, uint length, int coordinates, out uint read);

    /// <summary>Closes a handle (the console buffer the readiness check opened).</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Whether it was closed.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>SECURITY_ATTRIBUTES: here only to make a handle inheritable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        /// <summary>nLength: the structure's size.</summary>
        public int Length;

        /// <summary>lpSecurityDescriptor: null, the default.</summary>
        public IntPtr SecurityDescriptor;

        /// <summary>bInheritHandle: true.</summary>
        public bool InheritHandle;
    }

    /// <summary>Opens a file or a console buffer.</summary>
    /// <param name="name">The name ("CONIN$", "CONOUT$").</param>
    /// <param name="access">The access rights.</param>
    /// <param name="share">The sharing mode.</param>
    /// <param name="security">Makes the handle inheritable.</param>
    /// <param name="disposition">OPEN_EXISTING (3).</param>
    /// <param name="flags">0.</param>
    /// <param name="template">Null.</param>
    /// <returns>The handle, or INVALID_HANDLE_VALUE (-1).</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);

    /// <summary>STARTUPINFOW, as CreateProcessW reads it; only the size, flags, window state and (group-ctrl-c) standard handles are set.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        /// <summary>cb: the structure's size.</summary>
        public int Size;

        /// <summary>lpReserved, unused.</summary>
        public string? Reserved;

        /// <summary>lpDesktop, unused.</summary>
        public string? Desktop;

        /// <summary>lpTitle, unused.</summary>
        public string? Title;

        /// <summary>dwX, unused.</summary>
        public int X;

        /// <summary>dwY, unused.</summary>
        public int Y;

        /// <summary>dwXSize, unused.</summary>
        public int XSize;

        /// <summary>dwYSize, unused.</summary>
        public int YSize;

        /// <summary>dwXCountChars, unused.</summary>
        public int XCountChars;

        /// <summary>dwYCountChars, unused.</summary>
        public int YCountChars;

        /// <summary>dwFillAttribute, unused.</summary>
        public int FillAttribute;

        /// <summary>dwFlags: STARTF_USESHOWWINDOW, or STARTF_USESTDHANDLES in group-ctrl-c.</summary>
        public int Flags;

        /// <summary>wShowWindow: SW_HIDE (0).</summary>
        public short ShowWindow;

        /// <summary>cbReserved2, unused.</summary>
        public short Reserved2Size;

        /// <summary>lpReserved2, unused.</summary>
        public IntPtr Reserved2;

        /// <summary>hStdInput: the console's input in group-ctrl-c (STARTF_USESTDHANDLES), else unused.</summary>
        public IntPtr StdInput;

        /// <summary>hStdOutput: the console's output in group-ctrl-c, else unused.</summary>
        public IntPtr StdOutput;

        /// <summary>hStdError: the console's output in group-ctrl-c, else unused.</summary>
        public IntPtr StdError;
    }

    /// <summary>PROCESS_INFORMATION: what CreateProcessW returns about the started process.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        /// <summary>hProcess: waited on, read for the exit code, terminated in kill mode.</summary>
        public IntPtr Process;

        /// <summary>hThread, unused.</summary>
        public IntPtr Thread;

        /// <summary>dwProcessId: the launcher's id, also its process group's in group-ctrl-c mode.</summary>
        public int ProcessId;

        /// <summary>dwThreadId, unused.</summary>
        public int ThreadId;
    }

    /// <summary>
    /// PROCESS_BASIC_INFORMATION, laid out as ntdll writes it (six pointer-sized fields on x64
    /// and ARM64): only <see cref="InheritedFromUniqueProcessId"/> is read; the others hold the
    /// layout in place, and a wrong layout would read another field as the parent.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        /// <summary>The exit status (NTSTATUS), unused: layout only.</summary>
        public IntPtr ExitStatus;

        /// <summary>The process environment block's address, unused: layout only.</summary>
        public IntPtr PebBaseAddress;

        /// <summary>The processor affinity, unused: layout only.</summary>
        public IntPtr AffinityMask;

        /// <summary>The base priority, unused: layout only.</summary>
        public IntPtr BasePriority;

        /// <summary>The process's own id, unused: layout only.</summary>
        public IntPtr UniqueProcessId;

        /// <summary>The parent's process id: tells this probe's ping from any other.</summary>
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>Starts a process with the given creation flags and window state.</summary>
    /// <param name="applicationName">The program's path.</param>
    /// <param name="commandLine">Its command line (writable, as CreateProcessW requires).</param>
    /// <param name="processAttributes">Null.</param>
    /// <param name="threadAttributes">Null.</param>
    /// <param name="inheritHandles">False with a new console (its own handles); true in group-ctrl-c, for the console handles in startupInfo.</param>
    /// <param name="creationFlags">CREATE_NEW_CONSOLE, or CREATE_NEW_PROCESS_GROUP alone in group-ctrl-c.</param>
    /// <param name="environment">Null: this process's environment.</param>
    /// <param name="currentDirectory">Null: this process's current directory.</param>
    /// <param name="startupInfo">The window state.</param>
    /// <param name="processInformation">Receives the process's handles and id.</param>
    /// <returns>Whether it started.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    /// <summary>Waits for a process handle.</summary>
    /// <param name="handle">The process handle.</param>
    /// <param name="milliseconds">How long to wait; 0 to only check.</param>
    /// <returns>0 when the process exited (WAIT_OBJECT_0), 0x102 on timeout.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>A process's exit code.</summary>
    /// <param name="process">The process handle.</param>
    /// <param name="exitCode">Receives the code.</param>
    /// <returns>Whether it was read.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>Kills a process, as Process.Kill does (the launcher alone, not what it started).</summary>
    /// <param name="process">The process handle.</param>
    /// <param name="exitCode">The exit code it gets.</param>
    /// <returns>Whether it was killed.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Detaches this process from its console.</summary>
    /// <returns>Whether it had one to leave.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    /// <summary>Attaches this process to another process's console.</summary>
    /// <param name="processId">The process whose console to join.</param>
    /// <returns>Whether it joined.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint processId);

    /// <summary>With a null handler and true: this process ignores Ctrl+C (the flag children inherit).</summary>
    /// <param name="handler">Null.</param>
    /// <param name="add">True to ignore.</param>
    /// <returns>Whether it was set.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    /// <summary>Sends a console signal to the processes of this process's console.</summary>
    /// <param name="ctrlEvent">0 for CTRL_C_EVENT.</param>
    /// <param name="processGroupId">0 for every process attached to the console, else that group alone.</param>
    /// <returns>Whether it was sent.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    /// <summary>ProcessBasicInformation (class 0) of a process.</summary>
    /// <param name="process">The process handle.</param>
    /// <param name="informationClass">0.</param>
    /// <param name="information">Receives the information.</param>
    /// <param name="size">Its size.</param>
    /// <param name="returned">The bytes written.</param>
    /// <returns>An NTSTATUS: 0 on success.</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int size, out int returned);
}
