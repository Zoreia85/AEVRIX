using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Owns a Windows primary access token created with DISABLE_MAX_PRIVILEGE.
/// This is a privilege-reduction primitive only: it does not itself enforce
/// filesystem or network isolation and must not be used to attest either.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestrictedTokenLease : IDisposable
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const int TokenPrivileges = 3;
    private const int TokenType = 8;
    private const int TokenPrimary = 1;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;

    private readonly SafeAccessTokenHandle _token;

    private WindowsRestrictedTokenLease(SafeAccessTokenHandle token, int enabledPrivilegeCount)
    {
        _token = token;
        EnabledPrivilegeCount = enabledPrivilegeCount;
    }

    public int EnabledPrivilegeCount { get; }
    public bool IsPrimaryToken => !_token.IsClosed && ReadTokenType(_token) == TokenPrimary;
    public bool MaximumPrivilegesDisabled => EnabledPrivilegeCount <= 1;
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

    public static WindowsRestrictedTokenLease Create()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenQuery | TokenDuplicate | TokenAssignPrimary,
                out var processToken))
        {
            throw Win32("OpenProcessToken failed.");
        }

        using (processToken)
        {
            if (!CreateRestrictedToken(
                    processToken,
                    DisableMaxPrivilege,
                    0,
                    IntPtr.Zero,
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

                return new WindowsRestrictedTokenLease(restrictedToken, enabledPrivileges);
            }
            catch
            {
                restrictedToken.Dispose();
                throw;
            }
        }
    }

    internal static bool ProcessTokenHasMaximumPrivilegesDisabled(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Process handle cannot be null.", nameof(processHandle));
        }

        if (!OpenProcessToken(processHandle, TokenQuery, out var processToken))
        {
            throw Win32("OpenProcessToken(child) failed.");
        }

        using (processToken)
        {
            return CountEnabledPrivileges(processToken) <= 1;
        }
    }

    public void Dispose() => _token.Dispose();

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

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
}
