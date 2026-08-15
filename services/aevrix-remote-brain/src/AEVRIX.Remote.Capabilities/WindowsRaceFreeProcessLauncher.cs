using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        WindowsJobObjectLease jobLease,
        bool restrictedTokenEnforced,
        bool appContainerEnforced)
    {
        Process = process;
        _stdout = stdout;
        _stderr = stderr;
        JobLease = jobLease;
        RestrictedTokenEnforced = restrictedTokenEnforced;
        AppContainerEnforced = appContainerEnforced;
    }

    internal Process Process { get; }
    internal Stream StandardOutput => _stdout;
    internal Stream StandardError => _stderr;
    internal WindowsJobObjectLease JobLease { get; }
    internal bool RestrictedTokenEnforced { get; }
    internal bool AppContainerEnforced { get; }

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
/// Windows-only launcher that creates the child with CREATE_SUSPENDED, optionally under a
/// DISABLE_MAX_PRIVILEGE primary token and/or an AppContainer SECURITY_CAPABILITIES attribute,
/// assigns the native process handle to an already-configured Job Object, and resumes the primary
/// thread only after assignment and token/AppContainer verification succeed. Handle inheritance is
/// restricted with STARTUPINFOEX so the child receives only its three governed standard-I/O handles.
/// </summary>
internal static class WindowsRaceFreeProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int ErrorInsufficientBuffer = 122;
    private const uint TokenQuery = 0x0008;
    private const int TokenIsAppContainerInformationClass = 29;
    private static readonly UIntPtr ProcThreadAttributeHandleList = new(0x00020002u);
    private static readonly UIntPtr ProcThreadAttributeSecurityCapabilities = new(0x00020009u);

    internal static WindowsRaceFreeProcessLaunch Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        WindowsJobObjectPolicy jobPolicy,
        WindowsRestrictedTokenLease? restrictedToken = null,
        WindowsAppContainerProfileLease? appContainerProfile = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Race-free suspended process launch requires Windows.");
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(jobPolicy);
        jobPolicy.Validate();

        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
        IntPtr securityCapabilities = IntPtr.Zero;
        IntPtr environmentPointer = IntPtr.Zero;
        ProcessInformation processInfo = default;
        WindowsJobObjectLease? jobLease = null;
        Process? process = null;
        var ownershipTransferred = false;
        var restrictedTokenEnforced = false;
        var appContainerEnforced = false;

        try
        {
            attributeList = BuildProcessAttributeList(
                stdin.ClientSafePipeHandle.DangerousGetHandle(),
                stdout.ClientSafePipeHandle.DangerousGetHandle(),
                stderr.ClientSafePipeHandle.DangerousGetHandle(),
                appContainerProfile,
                out handleList,
                out securityCapabilities);

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
                    hStdInput = stdin.ClientSafePipeHandle.DangerousGetHandle()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
            environmentPointer = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
            var creationFlags = CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;

            var created = restrictedToken is null
                ? NativeMethods.CreateProcessW(
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    creationFlags,
                    environmentPointer,
                    workingDirectory,
                    ref startup,
                    out processInfo)
                : NativeMethods.CreateProcessAsUserW(
                    restrictedToken.DangerousTokenHandle,
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    creationFlags,
                    environmentPointer,
                    workingDirectory,
                    ref startup,
                    out processInfo);

            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    restrictedToken is null
                        ? "Could not create the governed adapter process in suspended state."
                        : "Could not create the governed adapter process with the restricted token.");
            }

            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            stdin.DisposeLocalCopyOfClientHandle();
            stdin.Dispose();

            try
            {
                if (restrictedToken is not null)
                {
                    restrictedTokenEnforced = WindowsRestrictedTokenLease.ProcessTokenHasMaximumPrivilegesDisabled(processInfo.hProcess);
                    if (!restrictedTokenEnforced)
                    {
                        throw new InvalidOperationException("Child process token did not retain the required maximum-privilege reduction.");
                    }
                }

                if (appContainerProfile is not null)
                {
                    appContainerEnforced = ProcessTokenIsAppContainer(processInfo.hProcess);
                    if (!appContainerEnforced)
                    {
                        throw new InvalidOperationException("Child process did not retain the required AppContainer identity.");
                    }
                }

                jobLease = WindowsJobObjectLease.CreateAndAssign(processInfo.hProcess, jobPolicy);
                process = Process.GetProcessById(checked((int)processInfo.dwProcessId));

                var previousSuspendCount = NativeMethods.ResumeThread(processInfo.hThread);
                if (previousSuspendCount == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the governed adapter primary thread.");
                }

                ownershipTransferred = true;
                return new WindowsRaceFreeProcessLaunch(
                    process,
                    stdout,
                    stderr,
                    jobLease,
                    restrictedTokenEnforced,
                    appContainerEnforced);
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
            if (environmentPointer != IntPtr.Zero) Marshal.FreeHGlobal(environmentPointer);
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (securityCapabilities != IntPtr.Zero) Marshal.FreeHGlobal(securityCapabilities);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (processInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hProcess);
            if (!ownershipTransferred)
            {
                stdout.Dispose();
                stderr.Dispose();
                stdin.Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr BuildProcessAttributeList(
        IntPtr stdinHandle,
        IntPtr stdoutHandle,
        IntPtr stderrHandle,
        WindowsAppContainerProfileLease? appContainerProfile,
        out IntPtr handleList,
        out IntPtr securityCapabilities)
    {
        handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
        securityCapabilities = IntPtr.Zero;
        Marshal.WriteIntPtr(handleList, 0, stdinHandle);
        Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutHandle);
        Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrHandle);

        var attributeCount = appContainerProfile is null ? 1 : 2;
        IntPtr size = IntPtr.Zero;
        if (NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer
            || size == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(handleList);
            handleList = IntPtr.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the process attribute-list size.");
        }

        var attributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize the process attribute list.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeHandleList,
                    handleList,
                    new UIntPtr(checked((uint)(IntPtr.Size * 3))),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited handles for the governed adapter process.");
            }

            if (appContainerProfile is not null)
            {
                var value = new SecurityCapabilities
                {
                    AppContainerSid = appContainerProfile.DangerousSid,
                    Capabilities = IntPtr.Zero,
                    CapabilityCount = 0,
                    Reserved = 0
                };
                var securityCapabilitiesSize = Marshal.SizeOf<SecurityCapabilities>();
                securityCapabilities = Marshal.AllocHGlobal(securityCapabilitiesSize);
                Marshal.StructureToPtr(value, securityCapabilities, false);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributeSecurityCapabilities,
                        securityCapabilities,
                        new UIntPtr(checked((uint)securityCapabilitiesSize)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind AppContainer security capabilities to the governed adapter process.");
                }
            }

            return attributeList;
        }
        catch
        {
            NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            if (securityCapabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(securityCapabilities);
                securityCapabilities = IntPtr.Zero;
            }
            Marshal.FreeHGlobal(handleList);
            handleList = IntPtr.Zero;
            throw;
        }
    }

    private static bool ProcessTokenIsAppContainer(IntPtr processHandle)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open child process token for AppContainer verification.");
        }

        try
        {
            var value = 0;
            if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    TokenIsAppContainerInformationClass,
                    ref value,
                    sizeof(int),
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query child process AppContainer state.");
            }
            return value != 0;
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
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
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUserW(
            IntPtr token,
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            ref int tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            uint flags,
            ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            UIntPtr attribute,
            IntPtr value,
            UIntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

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
