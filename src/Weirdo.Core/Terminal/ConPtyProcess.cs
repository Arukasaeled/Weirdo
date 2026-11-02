using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Weirdo.Core.Terminal;

internal sealed class ConPtyProcess : IDisposable
{
    private readonly object _nativeLock = new();
    private readonly object _writeLock = new();
    private FileStream? _input;
    private FileStream? _output;
    private SafeKernelHandle? _process;
    private SafeKernelHandle? _thread;
    private SafeKernelHandle? _job;
    private IntPtr _pseudoConsole;
    private Task? _readerTask;
    private Task? _waitTask;
    private int _started;
    private int _disposed;
    private int _terminated;
    public int ProcessId { get; private set; }

    public ConPtyProcess(LaunchSpec launch, int columns, int rows)
    {
        ValidateSize(columns, rows);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or newer.");
        if (string.IsNullOrWhiteSpace(launch.CommandLine) || launch.CommandLine.Length >= 32767 || launch.CommandLine.Contains('\0'))
            throw new ArgumentException("Invalid terminal command line.", nameof(launch));
        var cwd = Path.GetFullPath(launch.Cwd);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);

        SafeFileHandle? ptyInput = null, ptyOutput = null, parentInput = null, parentOutput = null;
        var attributeList = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            if (!NativeMethods.CreatePipe(out ptyInput, out parentInput, IntPtr.Zero, 0)) ThrowLastError("CreatePipe(input)");
            if (!NativeMethods.CreatePipe(out parentOutput, out ptyOutput, IntPtr.Zero, 0)) ThrowLastError("CreatePipe(output)");
            Marshal.ThrowExceptionForHR(NativeMethods.CreatePseudoConsole(new((short)columns, (short)rows), ptyInput, ptyOutput, 0, out _pseudoConsole));
            _input = new FileStream(parentInput, FileAccess.Write, 4096, false); parentInput = null;
            _output = new FileStream(parentOutput, FileAccess.Read, 16384, false); parentOutput = null;

            var bytes = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributeList = Marshal.AllocHGlobal(bytes);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes)) ThrowLastError("InitializeProcThreadAttributeList");
            attributesInitialized = true;
            if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PseudoConsoleAttribute, _pseudoConsole, new(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                ThrowLastError("UpdateProcThreadAttribute(ConPTY)");

            _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (_job.IsInvalid) ThrowLastError("CreateJobObject");
            var limits = new NativeMethods.JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
            if (!NativeMethods.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<NativeMethods.JobExtendedLimitInformation>()))
                ThrowLastError("SetInformationJobObject");

            environment = Marshal.StringToHGlobalUni(BuildEnvironment(launch.Environment));
            var startup = new NativeMethods.StartupInfoEx { AttributeList = attributeList };
            startup.StartupInfo.Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
            // Explicit NULL standard handles prevent CreateProcess from duplicating
            // the broker's redirected stdin/stdout instead of using the new PTY.
            // See microsoft/terminal discussion 15814 (Windows console team).
            startup.StartupInfo.Flags = 0x00000100; // STARTF_USESTDHANDLES
            var flags = NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateSuspended;
            if (!NativeMethods.CreateProcessW(null, new StringBuilder(launch.CommandLine), IntPtr.Zero, IntPtr.Zero, false, flags, environment, cwd, ref startup, out var info))
                ThrowLastError("CreateProcess");
            _process = new SafeKernelHandle(info.Process);
            _thread = new SafeKernelHandle(info.Thread);
            ProcessId = checked((int)info.ProcessId);
            // Assign before the child can spawn anything. No global PID scanning/kill.
            if (!NativeMethods.AssignProcessToJobObject(_job, _process)) ThrowLastError("AssignProcessToJobObject");
            // Once the child is attached, only ConPTY owns its endpoints. Keeping
            // our duplicate output writer alive would prevent the reader seeing EOF.
            ptyInput.Dispose(); ptyInput = null;
            ptyOutput.Dispose(); ptyOutput = null;
        }
        catch
        {
            if (_process is { IsInvalid: false }) NativeMethods.TerminateProcess(_process, 1);
            ptyInput?.Dispose(); ptyInput = null;
            ptyOutput?.Dispose(); ptyOutput = null;
            // A failed startup still has to drain ConPTY while closing it.
            if (_output is not null) _readerTask = Task.Factory.StartNew(() => Drain(null), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Cleanup();
            throw;
        }
        finally
        {
            if (attributesInitialized) NativeMethods.DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            ptyInput?.Dispose(); ptyOutput?.Dispose(); parentInput?.Dispose(); parentOutput?.Dispose();
        }
    }

    public void Start(Action<string, int> onOutput, Action<int> onExit)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Terminal already started.");
        _readerTask = Task.Factory.StartNew(() => Drain(onOutput), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (_thread is null || NativeMethods.ResumeThread(_thread) == uint.MaxValue)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
            Terminate();
            Cleanup();
            throw error;
        }
        _thread.Dispose(); _thread = null;
        _waitTask = Task.Factory.StartNew(() =>
        {
            var code = 1;
            try
            {
                if (_process is not null)
                {
                    NativeMethods.WaitForSingleObject(_process, NativeMethods.Infinite);
                    if (NativeMethods.GetExitCodeProcess(_process, out var nativeCode)) code = unchecked((int)nativeCode);
                }
            }
            finally
            {
                Cleanup();
                onExit(code);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Write(string text)
    {
        if (text.Length > 1_048_576) throw new ArgumentException("Terminal input is too large.");
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            if (_input is null || Volatile.Read(ref _terminated) != 0) throw new InvalidOperationException("Terminal has exited.");
            _input.Write(bytes);
            _input.Flush();
        }
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        lock (_nativeLock)
        {
            if (_pseudoConsole == IntPtr.Zero) return;
            Marshal.ThrowExceptionForHR(NativeMethods.ResizePseudoConsole(_pseudoConsole, new((short)columns, (short)rows)));
        }
    }

    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        lock (_nativeLock)
        {
            if (_job is { IsClosed: false, IsInvalid: false }) NativeMethods.TerminateJobObject(_job, 0xC000013A);
        }
    }

    private void Drain(Action<string, int>? callback)
    {
        var bytes = new byte[16_384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var pendingBytes = 0;
        try
        {
            while (_output is not null)
            {
                var count = _output.Read(bytes, 0, bytes.Length);
                if (count == 0) break;
                pendingBytes += count;
                var charCount = decoder.GetChars(bytes.AsSpan(0, count), chars, false);
                if (charCount > 0) { callback?.Invoke(new string(chars, 0, charCount), pendingBytes); pendingBytes = 0; }
            }
            var remaining = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, true);
            if (remaining > 0) callback?.Invoke(new string(chars, 0, remaining), pendingBytes);
        }
        catch (IOException) { /* Broken pipe means child/ConPTY has closed. */ }
        catch (ObjectDisposedException) { }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _terminated, 1);
        IntPtr pseudo;
        lock (_nativeLock)
        {
            pseudo = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
            // Terminate any descendants that outlived the direct child.
            _job?.Dispose();
            _job = null;
        }
        // ClosePseudoConsole can wait for output to drain. Never call on the reader
        // or hold the output/write lock here; the read loop is still running.
        if (pseudo != IntPtr.Zero) NativeMethods.ClosePseudoConsole(pseudo);
        _readerTask?.GetAwaiter().GetResult();
        lock (_writeLock) { _input?.Dispose(); _input = null; }
        _output?.Dispose(); _output = null;
        _thread?.Dispose(); _thread = null;
        _process?.Dispose(); _process = null;
    }

    public void Dispose()
    {
        Terminate();
        if (_waitTask is null) Cleanup();
        else if (Task.CurrentId != _waitTask.Id) _waitTask.GetAwaiter().GetResult();
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns < 2 || columns > 4096 || rows < 1 || rows > 4096)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal size must be 2..4096 columns and 1..4096 rows.");
    }

    private static string BuildEnvironment(Dictionary<string, string> overrides)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string key && item.Value is string value) environment[key] = value;
        foreach (var (key, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
                throw new ArgumentException("Environment names and values must not contain NUL; names cannot contain '='.");
            environment[key] = value;
        }
        environment.TryAdd("TERM", "xterm-256color");
        environment.TryAdd("COLORTERM", "truecolor");
        environment.TryAdd("TERM_PROGRAM", "Weirdo");
        return string.Join('\0', environment.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
    }

    private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed");
}
