// Based on the user's Cyber PowerShell ConPTY prototype (agent/mvp-usable-shell).
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeTerminal.Host;

public sealed class ConPtySession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

    private readonly string _executable;
    private readonly string _arguments;
    private readonly string _workingDirectory;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IntPtr _pseudoConsole;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private Process? _process;
    private bool _disposed;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;
    public event Action<string>? TransportFaulted;

    public ConPtySession(string executable, string arguments, string workingDirectory)
    {
        _executable = executable;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    public Task StartAsync(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pseudoConsole != IntPtr.Zero)
        {
            throw new InvalidOperationException("La sessione ConPTY è già avviata.");
        }

        IntPtr ptyInputRead = IntPtr.Zero;
        IntPtr hostInputWrite = IntPtr.Zero;
        IntPtr hostOutputRead = IntPtr.Zero;
        IntPtr ptyOutputWrite = IntPtr.Zero;

        try
        {
            EnsureWin32(CreatePipe(out ptyInputRead, out hostInputWrite, IntPtr.Zero, 0), "CreatePipe(input)");
            EnsureWin32(CreatePipe(out hostOutputRead, out ptyOutputWrite, IntPtr.Zero, 0), "CreatePipe(output)");

            EnsureWin32(SetHandleInformation(hostInputWrite, HandleFlagInherit, 0), "SetHandleInformation(input)");
            EnsureWin32(SetHandleInformation(hostOutputRead, HandleFlagInherit, 0), "SetHandleInformation(output)");

            var size = ToCoord(columns, rows);
            var hr = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out _pseudoConsole);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            CloseHandle(ptyInputRead);
            ptyInputRead = IntPtr.Zero;
            CloseHandle(ptyOutputWrite);
            ptyOutputWrite = IntPtr.Zero;

            _inputStream = new FileStream(
                new SafeFileHandle(hostInputWrite, ownsHandle: true),
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false);
            hostInputWrite = IntPtr.Zero;

            _outputStream = new FileStream(
                new SafeFileHandle(hostOutputRead, ownsHandle: true),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
            hostOutputRead = IntPtr.Zero;

            StartChildProcess();
            _ = Task.Run(() => ReadOutputAsync(_cancellation.Token));
            return Task.CompletedTask;
        }
        catch
        {
            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }

            _inputStream?.Dispose();
            _outputStream?.Dispose();
            _inputStream = null;
            _outputStream = null;
            throw;
        }
        finally
        {
            CloseIfValid(ptyInputRead);
            CloseIfValid(hostInputWrite);
            CloseIfValid(hostOutputRead);
            CloseIfValid(ptyOutputWrite);
        }
    }

    public async Task WriteAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || _inputStream is null || _disposed)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeLock.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            await _inputStream.WriteAsync(bytes, _cancellation.Token).ConfigureAwait(false);
            await _inputStream.FlushAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_pseudoConsole == IntPtr.Zero || _disposed)
        {
            return;
        }

        var hr = ResizePseudoConsole(_pseudoConsole, ToCoord(columns, rows));
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private void StartChildProcess()
    {
        nuint attributeListSize = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

        var attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
        try
        {
            EnsureWin32(
                InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize),
                "InitializeProcThreadAttributeList");

            EnsureWin32(
                UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero),
                "UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE)");

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder($"\"{_executable}\" {_arguments}".TrimEnd());
            EnsureWin32(
                CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    _workingDirectory,
                    ref startupInfo,
                    out var processInfo),
                $"CreateProcess({_executable})");

            try
            {
                _process = Process.GetProcessById((int)processInfo.dwProcessId);
                _process.EnableRaisingEvents = true;
                _process.Exited += (_, _) =>
                {
                    try
                    {
                        Exited?.Invoke(_process.ExitCode);
                    }
                    catch
                    {
                        Exited?.Invoke(-1);
                    }
                };
            }
            finally
            {
                CloseIfValid(processInfo.hThread);
                CloseIfValid(processInfo.hProcess);
            }
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        if (_outputStream is null) return;

        // StreamReader.ReadAsync(Memory<char>) may hold a short prompt until
        // its buffer is filled. Read raw bytes instead: an interactive shell
        // must forward *each* available fragment to xterm immediately.
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _outputStream.ReadAsync(bytes.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                var decoded = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (decoded > 0)
                    OutputReceived?.Invoke(new string(chars, 0, decoded));
            }
            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            if (remaining > 0) OutputReceived?.Invoke(new string(chars, 0, remaining));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (IOException) when (_disposed) { }
        catch (IOException ex) { TransportFaulted?.Invoke(ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort shutdown for the host.
        }

        _inputStream?.Dispose();
        _outputStream?.Dispose();

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        _process?.Dispose();
        _writeLock.Dispose();
        _cancellation.Dispose();
    }

    private static COORD ToCoord(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 1, short.MaxValue),
        Y = (short)Math.Clamp(rows, 1, short.MaxValue)
    };

    private static void EnsureWin32(bool success, string operation)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        nuint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
}
