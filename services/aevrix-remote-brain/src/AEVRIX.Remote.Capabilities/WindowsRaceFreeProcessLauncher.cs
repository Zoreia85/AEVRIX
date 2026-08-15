using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Aevrix.Remote.Capabilities;

internal sealed class WindowsRaceFreeProcessLaunch : IDisposable
{
    private readonly AnonymousPipeServerStream _stdout;
    private readonly AnonymousPipeServerStream _stderr;
    private bool _disposed;

    internal WindowsRaceFreeProcessLaunch(
        Process process,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr,
        WindowsJobObjectLease jobLease)
    {
        Process = process;
        _stdout = stdout;
        _stderr = stderr;
        JobLease = jobLease;
    }

    internal Process Process { get; }
    internal Stream StandardOutput => _stdout;
    internal Stream StandardError => _stderr;
    internal WindowsJobObjectLease JobLease { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stdout.Dispose();
        _stderr.Dispose();
        Process.Dispose();
        JobLease.Dispose();
    }
}

/// <summary>
/// Windows-only launcher that creates the child with CREATE_SUSPENDED, assigns the native
/// process handle to an already-configured Job Object, and resumes the primary thread only
/// after assignment succeeds. The untrusted adapter therefore cannot execute before Job
/// limits apply. Standard input is closed; stdout/stderr are captured through inherited pipes.
/// </summary>
internal static class WindowsRaceFreeProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;

    internal static WindowsRaceFreeProcessLaunch Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        WindowsJobObjectPolicy jobPolicy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Race-free suspended process launch requires Windows.");
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(jobPolicy);
        jobPolicy.Validate();

        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
            hStdInput = stdin.ClientSafePipeHandle.DangerousGetHandle()
        };

        var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
        var environmentBlock = BuildEnvironmentBlock(environment);
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
        ProcessInformation processInfo = default;
        WindowsJobObjectLease? jobLease = null;
        Process? process = null;

        try
        {
            if (!NativeMethods.CreateProcessW(
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    environmentPointer,
                    workingDirectory,
                    ref startup,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the governed adapter process in suspended state.");
            }

            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            stdin.DisposeLocalCopyOfClientHandle();
            stdin.Dispose();

            try
            {
                jobLease = WindowsJobObjectLease.CreateAndAssign(processInfo.hProcess, jobPolicy);
                process = Process.GetProcessById(checked((int)processInfo.dwProcessId));

                var previousSuspendCount = NativeMethods.ResumeThread(processInfo.hThread);
                if (previousSuspendCount == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the governed adapter primary thread.");
                }

                return new WindowsRaceFreeProcessLaunch(
                    process,
                    Transfer(stdout),
                    Transfer(stderr),
                    jobLease);
            }
            catch
            {
                try { NativeMethods.TerminateProcess(processInfo.hProcess, 1); } catch { }
                process?.Dispose();
                jobLease?.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPointer);
            if (processInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hProcess);
        }
    }

    private static AnonymousPipeServerStream Transfer(AnonymousPipeServerStream stream)
    {
        // Ownership is transferred to WindowsRaceFreeProcessLaunch; suppress disposal by the caller's using scope.
        return new AnonymousPipeServerStream(PipeDirection.In, stream.SafePipeHandle);
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        builder.Append('\0');
        return builder.ToString();
    }

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteArgument(argument));
        }
        return builder.ToString();
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return value;
        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
