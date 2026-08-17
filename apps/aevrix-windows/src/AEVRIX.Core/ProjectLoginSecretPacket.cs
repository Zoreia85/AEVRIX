using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

/// <summary>
/// Disposable binary transport envelope for a credential lease. It is intended for short-lived transfer
/// into a protected browser/shared-memory boundary without creating additional managed secret strings.
/// The backing byte array is zeroed on dispose.
/// </summary>
public sealed class ProjectLoginSecretPacket : IDisposable
{
    private const int HeaderLength = 13;
    private const byte Version = 1;
    private static ReadOnlySpan<byte> Magic => "AXLG"u8;

    private byte[]? _buffer;

    private ProjectLoginSecretPacket(byte[] buffer)
    {
        _buffer = buffer;
    }

    public int Length => _buffer?.Length ?? throw new ObjectDisposedException(nameof(ProjectLoginSecretPacket));

    public ReadOnlyMemory<byte> Data =>
        _buffer ?? throw new ObjectDisposedException(nameof(ProjectLoginSecretPacket));

    public static ProjectLoginSecretPacket Create(
        ReadOnlyMemory<char> userName,
        ReadOnlyMemory<char> password)
    {
        if (userName.IsEmpty)
        {
            throw new ArgumentException("Login user name must not be empty.", nameof(userName));
        }
        if (password.IsEmpty)
        {
            throw new ArgumentException("Login password must not be empty.", nameof(password));
        }
        if (userName.Length > 320)
        {
            throw new ArgumentOutOfRangeException(nameof(userName));
        }
        if (password.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(password));
        }

        var userByteCount = Encoding.UTF8.GetByteCount(userName.Span);
        var passwordByteCount = Encoding.UTF8.GetByteCount(password.Span);
        var totalLength = checked(HeaderLength + userByteCount + passwordByteCount);
        var buffer = GC.AllocateUninitializedArray<byte>(totalLength);

        try
        {
            Magic.CopyTo(buffer.AsSpan(0, 4));
            buffer[4] = Version;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(5, 4), checked((uint)userByteCount));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(9, 4), checked((uint)passwordByteCount));

            var offset = HeaderLength;
            var userWritten = Encoding.UTF8.GetBytes(userName.Span, buffer.AsSpan(offset, userByteCount));
            if (userWritten != userByteCount)
            {
                throw new InvalidOperationException("Unexpected UTF-8 user-name encoding length.");
            }

            offset += userByteCount;
            var passwordWritten = Encoding.UTF8.GetBytes(password.Span, buffer.AsSpan(offset, passwordByteCount));
            if (passwordWritten != passwordByteCount)
            {
                throw new InvalidOperationException("Unexpected UTF-8 password encoding length.");
            }

            return new ProjectLoginSecretPacket(buffer);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }
    }

    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        var buffer = _buffer ?? throw new ObjectDisposedException(nameof(ProjectLoginSecretPacket));
        destination.Write(buffer, 0, buffer.Length);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}