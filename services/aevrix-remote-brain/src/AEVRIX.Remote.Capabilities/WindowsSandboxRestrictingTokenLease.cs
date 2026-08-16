using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Creates one primary Windows token that applies maximum-privilege reduction, Low Integrity and
/// an explicit sandbox restricting SID in the same CreateRestrictedToken operation. This is
/// required because Win32 intersects a new restricting-SID list with the existing list when the
/// input token is already restricted; layering the sandbox SID after a previous restricted-token
/// step can therefore never safely add a new restricting SID.
///
/// This primitive still does not claim filesystem isolation. The governed workspace must grant the
/// same sandbox SID and hostile proof must independently establish both read and write boundaries.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxRestrictingTokenLease : IDisposable
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaximumPrivilege = 0x00000001;
    private const uint SePrivilegeEnabledByDefault = 0x00000001;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint SeChangeNotifyPrivilegeLuidLowPart = 23;
    private const int TokenPrivileges = 3;
    private const int TokenType = 8;
    private const int TokenRestrictedSids = 11;
    private const int TokenIntegrityLevel = 25;
    private const int TokenPrimary = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const string LowIntegritySid = "S-1-16-4096";

    private readonly SafeAccessTokenHandle _token;

    private WindowsSandboxRestrictingTokenLease(
        SafeAccessTokenHandle token,
        string sandboxSid,
        bool maximumPrivilegesDisabled,
        bool lowIntegrityEnforced,
        bool restrictingSidPresent)
    {
        _token = token;
        SandboxSid = sandboxSid;
        MaximumPrivilegesDisabled = maximumPrivilegesDisabled;
        LowIntegrityEnforced = lowIntegrityEnforced;
        RestrictingSidPresent = restrictingSidPresent;
    }

    public string SandboxSid { get; }
    public bool MaximumPrivilegesDisabled { get; }
    public bool LowIntegrityEnforced { get; }
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

    /// <summary>
    /// Compatibility entry point for the current strict runtime pipeline. The prerequisite token
    /// is validated as a fail-closed precondition but is deliberately NOT used as the source token
    /// for the sandbox restriction. The strict token is derived from the original process identity
    /// so the AppContainer SID is installed in the first restricting-SID set rather than intersected
    /// with a previously restricted token.
    /// </summary>
    public static WindowsSandboxRestrictingTokenLease Create(
        WindowsRestrictedTokenLease prerequisiteToken,
        string sandboxSid)
    {
        ArgumentNullException.ThrowIfNull(prerequisiteToken);
        if (!prerequisiteToken.IsPrimaryToken
            || !prerequisiteToken.MaximumPrivilegesDisabled
            || !prerequisiteToken.LowIntegrityEnforced)
        {
            throw new InvalidOperationException(
                "Strict sandbox launch requires the reduced-token prerequisite to be a primary Low-Integrity token with maximum privileges disabled.");
        }

        return Create(sandboxSid);
    }

    public static WindowsSandboxRestrictingTokenLease Create(string sandboxSid)
    {
        if (string.IsNullOrWhiteSpace(sandboxSid) || sandboxSid.Length > 184)
        {
            throw new ArgumentException("Sandbox SID is missing or exceeds the supported SDDL length.", nameof(sandboxSid));
        }

        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault,
                out var sourceToken))
        {
            throw Win32("OpenProcessToken(strict sandbox) failed.");
        }

        using (sourceToken)
        {
            if (!ConvertStringSidToSidW(sandboxSid, out var sandboxSidPointer))
            {
                throw Win32("ConvertStringSidToSidW(sandbox) failed.");
            }

            try
            {
                if (!IsValidSid(sandboxSidPointer))
                {
                    throw new InvalidDataException("Sandbox SID conversion returned an invalid SID.");
                }

                var restrictedSidBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
                try
                {
                    Marshal.StructureToPtr(
                        new SidAndAttributes { Sid = sandboxSidPointer, Attributes = 0 },
                        restrictedSidBuffer,
                        false);

                    if (!CreateRestrictedToken(
                            sourceToken.DangerousGetHandle(),
                            DisableMaximumPrivilege,
                            0,
                            IntPtr.Zero,
                            0,
                            IntPtr.Zero,
                            1,
                            restrictedSidBuffer,
                            out var sandboxToken))
                    {
                        throw Win32("CreateRestrictedToken(strict sandbox) failed.");
                    }

                    try
                    {
                        ApplyLowIntegrity(sandboxToken);

                        if (ReadTokenType(sandboxToken) != TokenPrimary)
                        {
                            throw new InvalidOperationException("Strict sandbox token is not a primary token.");
                        }

                        var maximumPrivilegesDisabled = MaximumPrivilegesAreDisabled(sandboxToken);
                        var lowIntegrityEnforced = ReadIntegrityRid(sandboxToken) == 0x1000;
                        var restrictingSidPresent = ContainsRestrictedSid(sandboxToken, sandboxSidPointer);
                        if (!maximumPrivilegesDisabled || !lowIntegrityEnforced || !restrictingSidPresent)
                        {
                            throw new InvalidOperationException(
                                "Strict sandbox token failed its reduced-privilege, Low Integrity or restricting-SID self-check.");
                        }

                        return new WindowsSandboxRestrictingTokenLease(
                            sandboxToken,
                            sandboxSid.Trim(),
                            maximumPrivilegesDisabled,
                            lowIntegrityEnforced,
                            restrictingSidPresent);
                    }
                    catch
                    {
                        sandboxToken.Dispose();
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
                _ = LocalFree(sandboxSidPointer);
            }
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

    private static void ApplyLowIntegrity(SafeAccessTokenHandle token)
    {
        if (!ConvertStringSidToSidW(LowIntegritySid, out var lowSid))
        {
            throw Win32("ConvertStringSidToSidW(low integrity) failed.");
        }

        try
        {
            if (!IsValidSid(lowSid))
            {
                throw new InvalidDataException("Low Integrity SID conversion returned an invalid SID.");
            }

            var label = new TokenMandatoryLabel
            {
                Label = new SidAndAttributes
                {
                    Sid = lowSid,
                    Attributes = SeGroupIntegrity
                }
            };
            var size = checked(Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(lowSid));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(label, buffer, false);
                if (!SetTokenInformation(token, TokenIntegrityLevel, buffer, size))
                {
                    throw Win32("SetTokenInformation(TokenIntegrityLevel) failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = LocalFree(lowSid);
        }
    }

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

    private static bool MaximumPrivilegesAreDisabled(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes <= 0 || error != ErrorInsufficientBuffer)
        {
            throw Win32("GetTokenInformation(TokenPrivileges) size query failed.");
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!GetTokenInformation(token, TokenPrivileges, buffer, requiredBytes, out _))
            {
                throw Win32("GetTokenInformation(TokenPrivileges) failed.");
            }

            var count = Marshal.ReadInt32(buffer);
            var entrySize = Marshal.SizeOf<LuidAndAttributes>();
            var offset = sizeof(uint);
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<LuidAndAttributes>(IntPtr.Add(buffer, offset + (index * entrySize)));
                var enabled = (entry.Attributes & (SePrivilegeEnabled | SePrivilegeEnabledByDefault)) != 0;
                if (enabled && (entry.Luid.HighPart != 0 || entry.Luid.LowPart != SeChangeNotifyPrivilegeLuidLowPart))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ReadIntegrityRid(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes <= 0 || error != ErrorInsufficientBuffer)
        {
            throw Win32("GetTokenInformation(TokenIntegrityLevel) size query failed.");
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredBytes, out _))
            {
                throw Win32("GetTokenInformation(TokenIntegrityLevel) failed.");
            }

            var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            if (label.Label.Sid == IntPtr.Zero || !IsValidSid(label.Label.Sid))
            {
                throw new InvalidDataException("Token integrity SID is invalid.");
            }

            var count = GetSidSubAuthorityCount(label.Label.Sid);
            if (count == IntPtr.Zero)
            {
                throw Win32("GetSidSubAuthorityCount failed.");
            }

            var subAuthorityCount = Marshal.ReadByte(count);
            if (subAuthorityCount == 0)
            {
                throw new InvalidDataException("Token integrity SID has no sub-authority RID.");
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, checked((uint)(subAuthorityCount - 1)));
            if (ridPointer == IntPtr.Zero)
            {
                throw Win32("GetSidSubAuthority failed.");
            }

            return unchecked((uint)Marshal.ReadInt32(ridPointer));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength);

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

    [DllImport("advapi32.dll")]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
}
