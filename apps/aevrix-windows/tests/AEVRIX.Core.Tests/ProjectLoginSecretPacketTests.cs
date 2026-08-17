using System.Buffers.Binary;
using System.Text;
using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectLoginSecretPacketTests
{
    [TestMethod]
    public void Create_EncodesUtf8LengthsAndPayloadWithoutExtraSecretStringConversion()
    {
        var user = "usuário@example.com".ToCharArray();
        var password = "sênha-安全-123".ToCharArray();
        using var packet = ProjectLoginSecretPacket.Create(user, password);
        var data = packet.Data.Span;

        CollectionAssert.AreEqual(new byte[] { (byte)'A', (byte)'X', (byte)'L', (byte)'G' }, data[..4].ToArray());
        Assert.AreEqual(1, data[4]);

        var userLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(5, 4)));
        var passwordLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(9, 4)));
        Assert.AreEqual(Encoding.UTF8.GetByteCount(user), userLength);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(password), passwordLength);

        var userDecoded = Encoding.UTF8.GetString(data.Slice(13, userLength));
        var passwordDecoded = Encoding.UTF8.GetString(data.Slice(13 + userLength, passwordLength));
        Assert.AreEqual(new string(user), userDecoded);
        Assert.AreEqual(new string(password), passwordDecoded);

        Array.Clear(user);
        Array.Clear(password);
    }

    [TestMethod]
    public void Dispose_ZeroesCapturedBackingMemoryAndRejectsFutureAccess()
    {
        var user = "user@example.com".ToCharArray();
        var password = "very-sensitive-password".ToCharArray();
        var packet = ProjectLoginSecretPacket.Create(user, password);
        var captured = packet.Data;
        Assert.IsTrue(captured.Span.Any(value => value != 0));

        packet.Dispose();

        Assert.IsTrue(captured.Span.ToArray().All(value => value == 0));
        Assert.ThrowsException<ObjectDisposedException>(() => _ = packet.Data);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = packet.Length);
        Array.Clear(user);
        Array.Clear(password);
    }

    [TestMethod]
    public void WriteTo_CopiesPacketThenDisposeStillZeroesOriginalBackingMemory()
    {
        var user = "u".ToCharArray();
        var password = "p".ToCharArray();
        var packet = ProjectLoginSecretPacket.Create(user, password);
        var captured = packet.Data;
        using var stream = new MemoryStream();

        packet.WriteTo(stream);
        packet.Dispose();

        Assert.IsTrue(stream.Length > 13);
        Assert.IsTrue(captured.Span.ToArray().All(value => value == 0));
        Array.Clear(user);
        Array.Clear(password);
    }

    [TestMethod]
    public void Create_RejectsEmptyOrOversizedCredentialParts()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            ProjectLoginSecretPacket.Create(ReadOnlyMemory<char>.Empty, "password".ToCharArray()));
        Assert.ThrowsException<ArgumentException>(() =>
            ProjectLoginSecretPacket.Create("user".ToCharArray(), ReadOnlyMemory<char>.Empty));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            ProjectLoginSecretPacket.Create(new string('u', 321).ToCharArray(), "password".ToCharArray()));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            ProjectLoginSecretPacket.Create("user".ToCharArray(), new string('p', 1025).ToCharArray()));
    }
}