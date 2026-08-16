using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Adds one explicit restricting SID to an already-reduced Windows primary token.
/// A restricting SID causes Windows access checks to require access to be granted by both the
/// token's normal SID set and its restricting SID set. This primitive intentionally does not
/// claim filesystem isolation: the target workspace still needs an ACL that grants the same
/// sandbox SID before a WorkspaceOnly backend can be attested.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxRestrictingTokenLease : IDisposable
{
    private const uint TokenQuery = 0x0008;
    private const int TokenType = 8;
    private const int TokenRestrictedSids = 11;
    private const int TokenPrimary = 1;
    private const int ErrorInsufficientBuffer = 122;

    private readonly SafeAccessTokenHandle _token;

    private WindowsSandboxRestrictingTokenLease(
        SafeAccessTokenHandle token,
        string sandboxSid,
        bool restrictingSidPresent)
    {
        _token = token;
        SandboxSid = sandboxSid;
        RestrictingSidPresent = restrictingSidPresent;
    }

    public string SandboxSid { get; }
    public bool RestrictingSidPresent { get; }
    public bool IsPrimaryToken => !_token.IsClosed && ReadTokenType(_token) == TokenPrimary;
    public bool IsClosed => _token.IsClosed;

    internal IntPtr DangerousTokenHandle
    {
        get
        {
            if (_token.IsClosed || _token.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(WindowsSandboxRestrictingTokenLease));
            }

            return _token.DangerousGetHandle();
        }
    }

    public static WindowsSandboxRestrictingTokenLease Create(
        WindowsRestrictedTokenLease baseToken,
        string sandboxSid)
    {
        ArgumentNullException.ThrowIfNull(baseToken);
        if (!baseToken.IsPrimaryToken
            || !baseToken.MaximumPrivilegesDisabled
            || !baseToken.LowIntegrityEnforced)
        {
            throw new InvalidOperationException(
                "Sandbox restricting SID requires a primary base token with reduced privileges and Low integrity.");
        }

        if (string.IsNullOrWhiteSpace(sandboxSid) || sandboxSid.Length > 184)
        {
            throw new ArgumentException("Sandbox SID is missing or exceeds the supported SDDL length.", nameof(sandboxSid));
        }

        if (!ConvertStringSidToSidW(sandboxSid, out var sid))
        {
            throw Win32("ConvertStringSidToSidW(sandbox) failed.");
        }

        try
        {
            if (!IsValidSid(sid))
            {
                throw new InvalidDataException("Sandbox SID conversion returned an invalid SID.");
            }

            var restrictedSidBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
            try
            {
                Marshal.StructureToPtr(
                    new SidAndAttributes { Sid = sid, Attributes = 0 },
                    restrictedSidBuffer,
                    false);

                if (!CreateRestrictedToken(
                        baseToken.DangerousTokenHandle,
                        0,
                        0,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        1,
                        restrictedSidBuffer,
                        out var restrictedToken))
                {
                    throw Win32("CreateRestrictedToken(sandbox SID) failed.");
                }

                try
                {
                    if (ReadTokenType(restrictedToken) != TokenPrimary)
                    {
                        throw new InvalidOperationException("Sandbox-restricted token is not a primary token.");
                    }

                    var present = ContainsRestrictedSid(restrictedToken, sid);
                    if (!present)
                    {
                        throw new InvalidOperationException(
                            "Sandbox restricting SID was not retained by the resulting token.");
                    }

                    return new WindowsSandboxRestrictingTokenLease(
                        restrictedToken,
                        sandboxSid.Trim(),
                        present);
                }
                catch
                {
                    restrictedToken.Dispose();
                    throw;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(restrictedSidBuffer);
            }
        }
        finally
        {
            _ = LocalFree(sid);
        }
    }

    /// <summary>
    /// Verifies the exact sandbox restricting SID on a child token while the process can still be
    /// suspended by the strict launcher. A false result must be treated as a launch failure.
    /// </summary>
    internal static bool ProcessTokenContainsRestrictingSid(IntPtr processHandle, string sandboxSid)
    {
        if (processHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process handle cannot be null.", nameof(processHandle));
        }

        if (string.IsNullOrWhiteSpace(sandboxSid) || sandboxSid.Length > 184)
        {
            throw new ArgumentException("Sandbox SID is missing or exceeds the supported SDDL length.", nameof(sandboxSid));
        }

        if (!OpenProcessToken(processHandle, TokenQuery, out var processToken))
        {
            throw Win32("OpenProcessToken(child sandbox SID) failed.");
        }

        using (processToken)
        {
            if (!ConvertStringSidToSidW(sandboxSid, out var sid))
            {
                throw Win32("ConvertStringSidToSidW(child sandbox) failed.");
            }

            try
            {
                if (!IsValidSid(sid))
                {
                    throw new InvalidDataException("Child sandbox SID conversion returned an invalid SID.");
                }

                return ContainsRestrictedSid(processToken, sid);
            }
            finally
            {
                _ = LocalFree(sid);
            }
        }
    }

    public void Dispose() => _token.Dispose();

    private static int ReadTokenType(SafeAccessTokenHandle token)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, TokenType, buffer, sizeof(int), out _))
            {
                throw Win32("GetTokenInformation(TokenType) failed.");
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ContainsRestrictedSid(SafeAccessTokenHandle token, IntPtr expectedSid)
    {
        _ = GetTokenInformation(token, TokenRestrictedSids, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes <= 0 || error != ErrorInsufficientBuffer)
        {
            throw Win32("GetTokenInformation(TokenRestrictedSids) size query failed.");
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!GetTokenInformation(token, TokenRestrictedSids, buffer, requiredBytes, out _))
            {
                throw Win32("GetTokenInformation(TokenRestrictedSids) failed.");
            }

            var sidCount = Marshal.ReadInt32(buffer);
            if (sidCount is < 1 or > 256)
            {
                throw new InvalidDataException("Restricted SID count is outside safe bounds.");
            }

            var entrySize = Marshal.SizeOf<SidAndAttributes>();
            var firstEntryOffset = IntPtr.Size == 8 ? 8 : sizeof(uint);
            for (var index = 0; index < sidCount; index++)
            {
                var entryOffset = checked(firstEntryOffset + (index * entrySize));
                if (entryOffset + entrySize > requiredBytes)
                {
                    throw new InvalidDataException("Restricted SID buffer is malformed.");
                }

                var entry = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, entryOffset));
                if (entry.Sid != IntPtr.Zero && EqualSid(entry.Sid, expectedSid))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Win32Exception Win32(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        IntPtr existingTokenHandle,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        IntPtr sidsToRestrict,
        out SafeAccessTokenHandle newTokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(
        string stringSid,
        out IntPtr sid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);
}
