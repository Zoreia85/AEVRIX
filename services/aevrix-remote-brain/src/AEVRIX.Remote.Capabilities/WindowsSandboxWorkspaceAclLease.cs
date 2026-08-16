using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aevrix.Remote.Capabilities;

public enum SandboxWorkspaceAccess
{
    ReadOnly,
    ReadWrite
}

/// <summary>
/// Adds an inheritable allow ACE for one explicit sandbox restricting SID to an exclusive,
/// ephemeral workspace directory and restores the original DACL on disposal.
/// This primitive deliberately does not claim complete filesystem isolation: the process token
/// must carry the same restricting SID and hostile in/out-of-workspace access tests must pass
/// before a backend may attest FilesystemIsolationEnforced=true.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxWorkspaceAclLease : IDisposable
{
    private const int SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint GrantAccess = 1;
    private const uint TrusteeIsSid = 0;
    private const uint TrusteeIsUnknown = 0;
    private const uint SubContainersAndObjectsInherit = 0x3;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;
    private const uint FileGenericExecute = 0x001200A0;
    private const byte AccessAllowedAceType = 0;

    private readonly string _workspaceRoot;
    private readonly IntPtr _originalSecurityDescriptor;
    private readonly IntPtr _originalDacl;
    private bool _disposed;

    private WindowsSandboxWorkspaceAclLease(
        string workspaceRoot,
        string sandboxSid,
        SandboxWorkspaceAccess access,
        IntPtr originalSecurityDescriptor,
        IntPtr originalDacl,
        bool aclGrantVerified)
    {
        _workspaceRoot = workspaceRoot;
        SandboxSid = sandboxSid;
        Access = access;
        _originalSecurityDescriptor = originalSecurityDescriptor;
        _originalDacl = originalDacl;
        AclGrantVerified = aclGrantVerified;
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string SandboxSid { get; }
    public SandboxWorkspaceAccess Access { get; }
    public bool AclGrantVerified { get; }
    public bool IsDisposed => _disposed;

    public static WindowsSandboxWorkspaceAclLease Create(
        string workspaceRoot,
        string sandboxSid,
        SandboxWorkspaceAccess access)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows sandbox workspace ACLs require Windows.");
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !Path.IsPathFullyQualified(workspaceRoot)
            || workspaceRoot.Length > 2_048)
        {
            throw new ArgumentException("Workspace root must be a bounded absolute path.", nameof(workspaceRoot));
        }

        var fullRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException("Sandbox workspace root does not exist.");
        }
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Sandbox workspace root cannot be a reparse point.");
        }

        if (string.IsNullOrWhiteSpace(sandboxSid) || sandboxSid.Length > 184)
        {
            throw new ArgumentException("Sandbox SID is missing or exceeds the supported SDDL length.", nameof(sandboxSid));
        }
        if (!Enum.IsDefined(access))
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if (!ConvertStringSidToSidW(sandboxSid, out var sid))
        {
            throw Win32("ConvertStringSidToSidW(sandbox ACL) failed.");
        }

        IntPtr originalDescriptor = IntPtr.Zero;
        IntPtr newAcl = IntPtr.Zero;
        try
        {
            if (!IsValidSid(sid))
            {
                throw new InvalidDataException("Sandbox SID conversion returned an invalid SID.");
            }

            var error = GetNamedSecurityInfoW(
                fullRoot,
                SeFileObject,
                DaclSecurityInformation,
                out _,
                out _,
                out var originalDacl,
                out _,
                out originalDescriptor);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "GetNamedSecurityInfoW(workspace) failed.");
            }

            var rights = access == SandboxWorkspaceAccess.ReadOnly
                ? FileGenericRead | FileGenericExecute
                : FileGenericRead | FileGenericWrite | FileGenericExecute;
            var explicitAccess = new ExplicitAccess
            {
                AccessPermissions = rights,
                AccessMode = GrantAccess,
                Inheritance = SubContainersAndObjectsInherit,
                Trustee = new Trustee
                {
                    MultipleTrustee = IntPtr.Zero,
                    MultipleTrusteeOperation = 0,
                    TrusteeForm = TrusteeIsSid,
                    TrusteeType = TrusteeIsUnknown,
                    Name = sid
                }
            };

            error = SetEntriesInAclW(1, [explicitAccess], originalDacl, out newAcl);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "SetEntriesInAclW(workspace) failed.");
            }

            error = SetNamedSecurityInfoW(
                fullRoot,
                SeFileObject,
                DaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                newAcl,
                IntPtr.Zero);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "SetNamedSecurityInfoW(workspace) failed.");
            }

            var verified = VerifyAllowAce(fullRoot, sid, rights);
            if (!verified)
            {
                _ = SetNamedSecurityInfoW(fullRoot, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, originalDacl, IntPtr.Zero);
                throw new InvalidOperationException("Sandbox workspace ACL grant could not be verified after application.");
            }

            var lease = new WindowsSandboxWorkspaceAclLease(
                fullRoot,
                sandboxSid.Trim(),
                access,
                originalDescriptor,
                originalDacl,
                verified);
            originalDescriptor = IntPtr.Zero;
            return lease;
        }
        finally
        {
            if (newAcl != IntPtr.Zero) _ = LocalFree(newAcl);
            if (sid != IntPtr.Zero) _ = LocalFree(sid);
            if (originalDescriptor != IntPtr.Zero) _ = LocalFree(originalDescriptor);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_originalSecurityDescriptor != IntPtr.Zero && Directory.Exists(_workspaceRoot))
            {
                var error = SetNamedSecurityInfoW(
                    _workspaceRoot,
                    SeFileObject,
                    DaclSecurityInformation,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _originalDacl,
                    IntPtr.Zero);
                if (error != 0)
                {
                    throw new Win32Exception(checked((int)error), "Could not restore the original workspace DACL.");
                }
            }
        }
        finally
        {
            if (_originalSecurityDescriptor != IntPtr.Zero) _ = LocalFree(_originalSecurityDescriptor);
        }
    }

    private static bool VerifyAllowAce(string path, IntPtr expectedSid, uint requiredRights)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            var error = GetNamedSecurityInfoW(
                path,
                SeFileObject,
                DaclSecurityInformation,
                out _,
                out _,
                out var dacl,
                out _,
                out descriptor);
            if (error != 0)
            {
                throw new Win32Exception(checked((int)error), "GetNamedSecurityInfoW(verify workspace) failed.");
            }
            if (dacl == IntPtr.Zero) return false;

            var info = new AclSizeInformation();
            if (!GetAclInformation(dacl, ref info, (uint)Marshal.SizeOf<AclSizeInformation>(), 2))
            {
                throw Win32("GetAclInformation(workspace) failed.");
            }
            if (info.AceCount > 4_096)
            {
                throw new InvalidDataException("Workspace ACL contains an excessive number of ACEs.");
            }

            for (uint index = 0; index < info.AceCount; index++)
            {
                if (!GetAce(dacl, index, out var ace) || ace == IntPtr.Zero)
                {
                    throw Win32("GetAce(workspace) failed.");
                }
                var header = Marshal.PtrToStructure<AceHeader>(ace);
                if (header.AceType != AccessAllowedAceType || header.AceSize < 12) continue;
                var mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
                var aceSid = IntPtr.Add(ace, 8);
                if (IsValidSid(aceSid)
                    && EqualSid(aceSid, expectedSid)
                    && (mask & requiredRights) == requiredRights)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor);
        }
    }

    private static Win32Exception Win32(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Trustee
    {
        public IntPtr MultipleTrustee;
        public uint MultipleTrusteeOperation;
        public uint TrusteeForm;
        public uint TrusteeType;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ExplicitAccess
    {
        public uint AccessPermissions;
        public uint AccessMode;
        public uint Inheritance;
        public Trustee Trustee;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        public uint AceCount;
        public uint AclBytesInUse;
        public uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AceHeader
    {
        public byte AceType;
        public byte AceFlags;
        public ushort AceSize;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfoW(
        string objectName,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfoW(
        string objectName,
        int objectType,
        uint securityInfo,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetEntriesInAclW(
        uint countOfExplicitEntries,
        [In] ExplicitAccess[] explicitEntries,
        IntPtr oldAcl,
        out IntPtr newAcl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAclInformation(IntPtr acl, ref AclSizeInformation aclInformation, uint aclInformationLength, int aclInformationClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(IntPtr acl, uint aceIndex, out IntPtr ace);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
