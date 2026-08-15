using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Creates one ephemeral per-user AppContainer profile with no capabilities and verifies that
/// Windows derives the same package SID from its opaque AEVRIX-generated profile name. The profile
/// name deliberately contains no project, user or evidence identifiers. The lease also resolves
/// the AppContainer-owned local-data path so launchers can construct the redirected environment
/// expected by Windows without reusing host TEMP/TMP locations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerProfileLease : IDisposable
{
    private const int S_OK = 0;
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);
    private const int MaximumCreateAttempts = 4;
    private readonly IntPtr _profileSid;
    private bool _disposed;

    private WindowsAppContainerProfileLease(
        string profileName,
        IntPtr profileSid,
        string appContainerSid,
        string profileFolderPath)
    {
        ProfileName = profileName;
        _profileSid = profileSid;
        AppContainerSid = appContainerSid;
        ProfileFolderPath = profileFolderPath;
    }

    public string ProfileName { get; }
    public string AppContainerSid { get; }
    public string ProfileFolderPath { get; }
    public string TempFolderPath => Path.Combine(ProfileFolderPath, "Temp");
    public bool ProfileCreated => !_disposed && _profileSid != IntPtr.Zero;
    public bool IsDisposed => _disposed;

    internal IntPtr DangerousSid
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_profileSid == IntPtr.Zero) throw new InvalidOperationException("AppContainer profile SID is unavailable.");
            return _profileSid;
        }
    }

    public static WindowsAppContainerProfileLease Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows AppContainer profiles require Windows.");
        }

        for (var attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            var profileName = "AEVRIX.Sandbox." + Guid.NewGuid().ToString("N");
            var hr = CreateAppContainerProfile(
                profileName,
                "AEVRIX isolated adapter",
                "Ephemeral AEVRIX adapter isolation profile",
                IntPtr.Zero,
                0,
                out var createdSid);

            if (hr != S_OK)
            {
                if (createdSid != IntPtr.Zero) _ = FreeSid(createdSid);
                if (hr == HResultAlreadyExists) continue;
                throw HResult(hr, "Could not create the ephemeral AppContainer profile.");
            }

            try
            {
                if (createdSid == IntPtr.Zero || !IsValidSid(createdSid))
                {
                    throw new InvalidDataException("CreateAppContainerProfile returned an invalid SID.");
                }

                var derivedHr = DeriveAppContainerSidFromAppContainerName(profileName, out var derivedSid);
                if (derivedHr != S_OK || derivedSid == IntPtr.Zero)
                {
                    throw HResult(derivedHr, "Could not derive the newly-created AppContainer SID.");
                }

                try
                {
                    if (!IsValidSid(derivedSid) || !EqualSid(createdSid, derivedSid))
                    {
                        throw new InvalidOperationException("Derived AppContainer SID does not match the created profile SID.");
                    }
                }
                finally
                {
                    _ = FreeSid(derivedSid);
                }

                var sidText = SidToString(createdSid);
                var folder = ResolveProfileFolder(sidText);
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder, "Temp"));

                return new WindowsAppContainerProfileLease(
                    profileName,
                    createdSid,
                    sidText,
                    folder);
            }
            catch
            {
                _ = FreeSid(createdSid);
                _ = DeleteAppContainerProfile(profileName);
                throw;
            }
        }

        throw new InvalidOperationException("Could not create a unique ephemeral AppContainer profile after bounded retries.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Exception? deletionError = null;
        try
        {
            var hr = DeleteAppContainerProfile(ProfileName);
            if (hr != S_OK)
            {
                deletionError = HResult(hr, "Could not delete the ephemeral AppContainer profile.");
            }
        }
        finally
        {
            if (_profileSid != IntPtr.Zero) _ = FreeSid(_profileSid);
        }

        if (deletionError is not null) throw deletionError;
    }

    private static string ResolveProfileFolder(string appContainerSid)
    {
        var hr = GetAppContainerFolderPath(appContainerSid, out var folderPointer);
        if (hr != S_OK || folderPointer == IntPtr.Zero)
        {
            throw HResult(hr, "Could not resolve the AppContainer local-data folder.");
        }

        try
        {
            var value = Marshal.PtrToStringUni(folderPointer);
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                throw new InvalidDataException("AppContainer local-data folder is invalid.");
            }
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderPointer);
        }
    }

    private static string SidToString(IntPtr sid)
    {
        if (!ConvertSidToStringSidW(sid, out var value) || value == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ConvertSidToStringSidW(AppContainer) failed.");
        }

        try
        {
            return Marshal.PtrToStringUni(value)
                ?? throw new InvalidDataException("AppContainer SID string conversion returned null.");
        }
        finally
        {
            _ = LocalFree(value);
        }
    }

    private static Exception HResult(int hr, string message) =>
        hr == S_OK
            ? new InvalidOperationException(message)
            : new COMException(message, hr);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        IntPtr capabilities,
        uint capabilityCount,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(string appContainerSid, out IntPtr path);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
