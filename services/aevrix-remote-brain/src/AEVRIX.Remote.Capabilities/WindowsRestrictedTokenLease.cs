using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Owns a Windows primary access token created with DISABLE_MAX_PRIVILEGE and explicitly
/// lowered to Low mandatory integrity. Callers may additionally request that the
/// BUILTIN\Administrators SID be made deny-only when present. Administrator-SID restriction is
/// opt-in because some legitimate Windows execution environments contain deny ACLs that make a
/// deny-only Administrators SID incompatible with otherwise valid adapters. This primitive does
/// not itself enforce filesystem or network isolation and must not be used to attest either.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestrictedTokenLease : IDisposable
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const int TokenGroups = 2;
    private const int TokenPrivileges = 3;
    private const int TokenType = 8;
    private const int TokenIntegrityLevel = 25;
    private const int TokenPrimary = 1;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint SeGroupEnabled = 0x00000004;
    private const uint SeGroupUseForDenyOnly = 0x00000010;
    private const uint SeGroupIntegrity = 0x00000020;
    private const int ErrorInsufficientBuffer = 122;
    private const uint LowIntegrityRid = 4096;
    private const string LowIntegritySid = "S-1-16-4096";
    private const string BuiltinAdministratorsSid = "S-1-5-32-544";

    private readonly SafeAccessTokenHandle _token;

    private WindowsRestrictedTokenLease(
        SafeAccessTokenHandle token,
        int enabledPrivilegeCount,
        bool lowIntegrityEnforced,
        bool administratorSidRestrictionRequested,
        bool administratorSidRestricted)
    {
        _token = token;
        EnabledPrivilegeCount = enabledPrivilegeCount;
        LowIntegrityEnforced = lowIntegrityEnforced;
        AdministratorSidRestrictionRequested = administratorSidRestrictionRequested;
        AdministratorSidRestricted = administratorSidRestricted;
    }

    public int EnabledPrivilegeCount { get; }
    public bool IsPrimaryToken => !_token.IsClosed && ReadTokenType(_token) == TokenPrimary;
    public bool MaximumPrivilegesDisabled => EnabledPrivilegeCount <= 1;
    public bool LowIntegrityEnforced { get; }
    public bool AdministratorSidRestrictionRequested { get; }
    public bool AdministratorSidRestricted { get; }
    public bool IsClosed => _token.IsClosed;

    internal IntPtr DangerousTokenHandle
    {
        get
        {
            if (_token.IsClosed || _token.IsInvalid)
            {
                throw new ObjectDisposedException(nameof(WindowsRestrictedTokenLease));
            }

            return _token.DangerousGetHandle();
        }
    }

    public static WindowsRestrictedTokenLease Create(bool restrictAdministratorSid = false)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault,
                out var processToken))
        {
            throw Win32("OpenProcessToken failed.");
        }

        using (processToken)
        {
            IntPtr administratorsSid = IntPtr.Zero;
            IntPtr disabledSidBuffer = IntPtr.Zero;
            try
            {
                if (restrictAdministratorSid)
                {
                    if (!ConvertStringSidToSidW(BuiltinAdministratorsSid, out administratorsSid))
                    {
                        throw Win32("ConvertStringSidToSidW(Administrators) failed.");
                    }

                    if (!IsValidSid(administratorsSid))
                    {
                        throw new InvalidDataException("Administrators SID conversion returned an invalid SID.");
                    }

                    disabledSidBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
                    Marshal.StructureToPtr(
                        new SidAndAttributes { Sid = administratorsSid, Attributes = 0 },
                        disabledSidBuffer,
                        false);
                }

                if (!CreateRestrictedToken(
                        processToken,
                        DisableMaxPrivilege,
                        restrictAdministratorSid ? 1u : 0u,
                        disabledSidBuffer,
                        0,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        out var restrictedToken))
                {
                    throw Win32("CreateRestrictedToken failed.");
                }

                try
                {
                    var tokenType = ReadTokenType(restrictedToken);
                    if (tokenType != TokenPrimary)
                    {
                        throw new InvalidOperationException("Restricted token is not a primary token.");
                    }

                    var enabledPrivileges = CountEnabledPrivileges(restrictedToken);
                    if (enabledPrivileges > 1)
                    {
                        throw new InvalidOperationException(
                            "Restricted token retained more enabled privileges than permitted by DISABLE_MAX_PRIVILEGE policy.");
                    }

                    var administratorSidRestricted = restrictAdministratorSid
                        && IsSidDenyOnlyOrAbsent(restrictedToken, administratorsSid);
                    if (restrictAdministratorSid && !administratorSidRestricted)
                    {
                        throw new InvalidOperationException(
                            "Restricted token retained an enabled BUILTIN\\Administrators SID.");
                    }

                    ApplyLowIntegrity(restrictedToken);
                    var lowIntegrityEnforced = ReadIntegrityRid(restrictedToken) == LowIntegrityRid;
                    if (!lowIntegrityEnforced)
                    {
                        throw new InvalidOperationException(
                            "Restricted token did not retain the required Low mandatory integrity level.");
                    }

                    return new WindowsRestrictedTokenLease(
                        restrictedToken,
                        enabledPrivileges,
                        lowIntegrityEnforced,
                        restrictAdministratorSid,
                        administratorSidRestricted);
                }
                catch
                {
                    restrictedToken.Dispose();
                    throw;
                }
            }
            finally
            {
                if (disabledSidBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(disabledSidBuffer);
                }

                if (administratorsSid != IntPtr.Zero)
                {
                    _ = LocalFree(administratorsSid);
                }
            }
        }
    }

    internal static bool ProcessTokenHasMaximumPrivilegesDisabled(IntPtr processHandle)
    {
        using var processToken = OpenChildProcessToken(processHandle);
        return CountEnabledPrivileges(processToken) <= 1;
    }

    internal static bool ProcessTokenHasLowIntegrity(IntPtr processHandle)
    {
        using var processToken = OpenChildProcessToken(processHandle);
        return ReadIntegrityRid(processToken) == LowIntegrityRid;
    }

    internal static bool ProcessTokenHasAdministratorSidRestricted(IntPtr processHandle)
    {
        using var processToken = OpenChildProcessToken(processHandle);
        if (!ConvertStringSidToSidW(BuiltinAdministratorsSid, out var administratorsSid))
        {
            throw Win32("ConvertStringSidToSidW(Administrators) failed.");
        }

        try
        {
            return IsSidDenyOnlyOrAbsent(processToken, administratorsSid);
        }
        finally
        {
            _ = LocalFree(administratorsSid);
        }
    }

    public void Dispose() => _token.Dispose();

    private static SafeAccessTokenHandle OpenChildProcessToken(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process handle cannot be null.", nameof(processHandle));
        }

        if (!OpenProcessToken(processHandle, TokenQuery, out var processToken))
        {
            throw Win32("OpenProcessToken(child) failed.");
        }

        return processToken;
    }

    private static void ApplyLowIntegrity(SafeAccessTokenHandle token)
    {
        if (!ConvertStringSidToSidW(LowIntegritySid, out var sid))
        {
            throw Win32("ConvertStringSidToSidW(LowIntegrity) failed.");
        }

        try
        {
            if (!IsValidSid(sid))
            {
                throw new InvalidDataException("Low-integrity SID conversion returned an invalid SID.");
            }

            var label = new TokenMandatoryLabel
            {
                Label = new SidAndAttributes
                {
                    Sid = sid,
                    Attributes = SeGroupIntegrity
                }
            };
            var labelSize = Marshal.SizeOf<TokenMandatoryLabel>();
            var length = checked(labelSize + GetLengthSid(sid));
            var buffer = Marshal.AllocHGlobal(labelSize);
            try
            {
                Marshal.StructureToPtr(label, buffer, false);
                if (!SetTokenInformation(
                        token,
                        TokenIntegrityLevel,
                        buffer,
                        length))
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
            _ = LocalFree(sid);
        }
    }

    private static int ReadTokenType(SafeAccessTokenHandle token)
    {
        var size = sizeof(int);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenType, buffer, size, out _))
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
                throw new InvalidDataException("Token integrity label contains an invalid SID.");
            }

            var subAuthorityCountPointer = GetSidSubAuthorityCount(label.Label.Sid);
            if (subAuthorityCountPointer == IntPtr.Zero)
            {
                throw new InvalidDataException("Token integrity SID subauthority count is unavailable.");
            }

            var subAuthorityCount = Marshal.ReadByte(subAuthorityCountPointer);
            if (subAuthorityCount == 0)
            {
                throw new InvalidDataException("Token integrity SID contains no subauthorities.");
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, checked((uint)(subAuthorityCount - 1)));
            if (ridPointer == IntPtr.Zero)
            {
                throw new InvalidDataException("Token integrity SID RID is unavailable.");
            }

            return unchecked((uint)Marshal.ReadInt32(ridPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsSidDenyOnlyOrAbsent(SafeAccessTokenHandle token, IntPtr sid)
    {
        _ = GetTokenInformation(token, TokenGroups, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes <= 0 || error != ErrorInsufficientBuffer)
        {
            throw Win32("GetTokenInformation(TokenGroups) size query failed.");
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!GetTokenInformation(token, TokenGroups, buffer, requiredBytes, out _))
            {
                throw Win32("GetTokenInformation(TokenGroups) failed.");
            }

            var groupCount = Marshal.ReadInt32(buffer);
            if (groupCount is < 0 or > 4096)
            {
                throw new InvalidDataException("Restricted token group count is outside safe bounds.");
            }

            var entrySize = Marshal.SizeOf<SidAndAttributes>();
            var firstEntryOffset = IntPtr.Size == 8 ? 8 : sizeof(uint);
            for (var index = 0; index < groupCount; index++)
            {
                var entryOffset = checked(firstEntryOffset + (index * entrySize));
                if (entryOffset + entrySize > requiredBytes)
                {
                    throw new InvalidDataException("Restricted token group buffer is malformed.");
                }

                var entry = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, entryOffset));
                if (entry.Sid == IntPtr.Zero || !EqualSid(entry.Sid, sid))
                {
                    continue;
                }

                return (entry.Attributes & SeGroupUseForDenyOnly) != 0
                    && (entry.Attributes & SeGroupEnabled) == 0;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CountEnabledPrivileges(SafeAccessTokenHandle token)
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

            var privilegeCount = Marshal.ReadInt32(buffer);
            if (privilegeCount is < 0 or > 256)
            {
                throw new InvalidDataException("Restricted token privilege count is outside safe bounds.");
            }

            var enabled = 0;
            const int firstEntryOffset = sizeof(uint);
            const int luidAndAttributesSize = 12;
            const int attributesOffset = 8;
            for (var index = 0; index < privilegeCount; index++)
            {
                var entryOffset = firstEntryOffset + (index * luidAndAttributesSize);
                if (entryOffset + luidAndAttributesSize > requiredBytes)
                {
                    throw new InvalidDataException("Restricted token privilege buffer is malformed.");
                }

                var attributes = unchecked((uint)Marshal.ReadInt32(buffer, entryOffset + attributesOffset));
                if ((attributes & SePrivilegeEnabled) != 0)
                {
                    enabled++;
                }
            }

            return enabled;
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
        SafeAccessTokenHandle existingTokenHandle,
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(
        string stringSid,
        out IntPtr sid);
}
