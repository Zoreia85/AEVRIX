using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Owns an ephemeral per-user Windows AppContainer profile and its SID.
/// This primitive establishes an isolated identity only; it does not by itself
/// attest filesystem or network isolation until a process is launched with the
/// SID and the requested authority is independently verified.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed class WindowsAppContainerProfileLease : IDisposable
{
    private const int S_OK = 0;
    private const int HResultAlreadyExists = unchecked((int)0x800700B7);
    private IntPtr _sid;
    private int _disposed;

    private WindowsAppContainerProfileLease(
        string profileName,
        IntPtr sid,
        string sidString,
        bool profileCreated,
        bool deleteOnDispose)
    {
        ProfileName = profileName;
        _sid = sid;
        SidString = sidString;
        ProfileCreated = profileCreated;
        DeleteOnDispose = deleteOnDispose;
    }

    public string ProfileName { get; }
    public string SidString { get; }
    public bool ProfileCreated { get; }
    public bool DeleteOnDispose { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal IntPtr DangerousSid
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _sid;
        }
    }

    public static WindowsAppContainerProfileLease CreateEphemeral(Guid projectId, string purpose)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 128)
        {
            throw new ArgumentException("AppContainer purpose is missing or too large.", nameof(purpose));
        }

        Span<byte> nonce = stackalloc byte[16];
        RandomNumberGenerator.Fill(nonce);
        var canonical = Encoding.UTF8.GetBytes($"{projectId:D}\n{purpose.Trim()}\n{Convert.ToHexString(nonce)}");
        var hash = SHA256.HashData(canonical);
        try
        {
            // Opaque project-bound identity: no raw project id or purpose is exposed in the profile name.
            var profileName = "AEVRIX." + Convert.ToHexString(hash.AsSpan(0, 15)).ToLowerInvariant();
            return Create(profileName, deleteOnDispose: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static WindowsAppContainerProfileLease Create(string profileName, bool deleteOnDispose = true)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            throw new PlatformNotSupportedException("AppContainer profiles require Windows 8 / Windows Server 2012 or newer.");
        }

        ValidateProfileName(profileName);

        IntPtr nativeSid = IntPtr.Zero;
        var hr = CreateAppContainerProfile(
            profileName,
            "AEVRIX isolated adapter",
            "Ephemeral AEVRIX adapter isolation profile",
            IntPtr.Zero,
            0,
            out nativeSid);

        var created = hr == S_OK;
        if (hr == HResultAlreadyExists)
        {
            hr = DeriveAppContainerSidFromAppContainerName(profileName, out nativeSid);
        }

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        if (nativeSid == IntPtr.Zero || !IsValidSid(nativeSid))
        {
            if (nativeSid != IntPtr.Zero)
            {
                _ = FreeSid(nativeSid);
            }
            throw new InvalidDataException("Windows returned an invalid AppContainer SID.");
        }

        IntPtr sidCopy = IntPtr.Zero;
        try
        {
            var sidLength = GetLengthSid(nativeSid);
            if (sidLength is < 8 or > 256)
            {
                throw new InvalidDataException("AppContainer SID length is outside safe bounds.");
            }

            sidCopy = Marshal.AllocHGlobal(sidLength);
            if (!CopySid(sidLength, sidCopy, nativeSid))
            {
                throw new InvalidOperationException("Failed to copy AppContainer SID.");
            }

            var sidString = SidToString(sidCopy);
            var lease = new WindowsAppContainerProfileLease(
                profileName,
                sidCopy,
                sidString,
                created,
                deleteOnDispose && created);
            sidCopy = IntPtr.Zero;
            return lease;
        }
        finally
        {
            _ = FreeSid(nativeSid);
            if (sidCopy != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(sidCopy);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sid = Interlocked.Exchange(ref _sid, IntPtr.Zero);
        if (sid != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(sid);
        }

        if (DeleteOnDispose)
        {
            var hr = DeleteAppContainerProfile(ProfileName);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }

    private static void ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Length > 64
            || profileName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ' ')))
        {
            throw new ArgumentException("AppContainer profile name is invalid.", nameof(profileName));
        }
    }

    private static string SidToString(IntPtr sid)
    {
        if (!ConvertSidToStringSidW(sid, out var stringSid) || stringSid == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to convert AppContainer SID to string form.");
        }

        try
        {
            return Marshal.PtrToStringUni(stringSid)
                ?? throw new InvalidDataException("AppContainer SID string is empty.");
        }
        finally
        {
            _ = LocalFree(stringSid);
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(
        string pszAppContainerName,
        string pszDisplayName,
        string pszDescription,
        IntPtr pCapabilities,
        uint dwCapabilityCount,
        out IntPtr ppSidAppContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string pszAppContainerName,
        out IntPtr ppsidAppContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string pszAppContainerName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr pSid);

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopySid(int nDestinationSidLength, IntPtr pDestinationSid, IntPtr pSourceSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr pSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
