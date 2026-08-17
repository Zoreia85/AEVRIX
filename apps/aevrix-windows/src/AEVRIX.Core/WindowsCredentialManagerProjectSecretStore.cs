using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aevrix.Core;

/// <summary>
/// Stores project credentials in the current Windows user's Credential Manager.
/// Entries use LOCAL_MACHINE persistence: they survive logon sessions on this PC but are not enterprise-roaming credentials.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerProjectSecretStore : IProjectCredentialSecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobBytes = 5 * 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public Task SaveAsync(
        Guid projectId,
        Guid credentialId,
        ProjectCredentialSecret secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateIds(projectId, credentialId);
        ArgumentNullException.ThrowIfNull(secret);
        secret.Validate();

        var envelope = new SecretEnvelope(1, projectId, credentialId, secret.UserName, secret.Password);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payload.Length > MaxCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidOperationException("Project credential exceeds the Windows Credential Manager payload limit.");
        }

        var blob = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, blob, payload.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName(projectId, credentialId),
                Comment = "AEVRIX project credential",
                CredentialBlobSize = (uint)payload.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "AEVRIX"
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the AEVRIX project credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            ZeroAndFree(blob, payload.Length);
        }

        return Task.CompletedTask;
    }

    public Task<ProjectCredentialSecret?> ReadAsync(
        Guid projectId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateIds(projectId, credentialId);

        if (!CredReadW(TargetName(projectId, credentialId), CredTypeGeneric, 0, out var nativePointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<ProjectCredentialSecret?>(null);
            }
            throw new Win32Exception(error, "Windows Credential Manager could not read the AEVRIX project credential.");
        }

        byte[]? payload = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(nativePointer);
            if (credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize == 0
                || credential.CredentialBlobSize > MaxCredentialBlobBytes)
            {
                throw new InvalidDataException("Stored AEVRIX project credential has an invalid payload size.");
            }

            payload = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, payload, 0, payload.Length);
            var envelope = JsonSerializer.Deserialize<SecretEnvelope>(payload, JsonOptions)
                ?? throw new InvalidDataException("Stored AEVRIX project credential is invalid.");
            if (envelope.Version != 1 || envelope.ProjectId != projectId || envelope.CredentialId != credentialId)
            {
                throw new InvalidDataException("Stored AEVRIX project credential identity does not match its requested scope.");
            }

            return Task.FromResult<ProjectCredentialSecret?>(
                new ProjectCredentialSecret(envelope.UserName, envelope.Password).Validate());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored AEVRIX project credential is malformed.", exception);
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
            CredFree(nativePointer);
        }
    }

    public Task DeleteAsync(
        Guid projectId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateIds(projectId, credentialId);

        if (!CredDeleteW(TargetName(projectId, credentialId), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Windows Credential Manager could not delete the AEVRIX project credential.");
            }
        }
        return Task.CompletedTask;
    }

    internal static string TargetName(Guid projectId, Guid credentialId)
    {
        ValidateIds(projectId, credentialId);
        return $"AEVRIX.ProjectCredential.{projectId:N}.{credentialId:N}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AEVRIX project credential storage requires Windows Credential Manager.");
        }
    }

    private static void ValidateIds(Guid projectId, Guid credentialId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("Credential id must not be empty.", nameof(credentialId));
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }
        for (var offset = 0; offset < length; offset++)
        {
            Marshal.WriteByte(pointer, offset, 0);
        }
        Marshal.FreeHGlobal(pointer);
    }

    private sealed record SecretEnvelope(
        int Version,
        Guid ProjectId,
        Guid CredentialId,
        string UserName,
        string Password);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW([In] ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
