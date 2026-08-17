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
        var userChars = "usuário@example.com".ToCharArray();
        var secretChars = "fixture-安全-123".ToCharArray();
        using var packet = ProjectLoginSecretPacket.Create(userChars, secretChars);
        var data = packet.Data.Span;

        CollectionAssert.AreEqual(new byte[] { (byte)'A', (byte)'X', (byte)'L', (byte)'G' }, data[..4].ToArray());
        Assert.AreEqual(1, data[4]);

        var userLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(5, 4)));
        var secretLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(9, 4)));
        Assert.AreEqual(Encoding.UTF8.GetByteCount(userChars), userLength);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(secretChars), secretLength);

        var userDecoded = Encoding.UTF8.GetString(data.Slice(13, userLength));
        var secretDecoded = Encoding.UTF8.GetString(data.Slice(13 + userLength, secretLength));
        Assert.AreEqual(new string(userChars), userDecoded);
        Assert.AreEqual(new string(secretChars), secretDecoded);

        Array.Clear(userChars);
        Array.Clear(secretChars);
    }

    [TestMethod]
    public void Dispose_ZeroesCapturedBackingMemoryAndRejectsFutureAccess()
    {
        var userChars = "user@example.com".ToCharArray();
        var secretChars = "synthetic-fixture-value".ToCharArray();
        var packet = ProjectLoginSecretPacket.Create(userChars, secretChars);
        var captured = packet.Data;
        Assert.IsTrue(captured.Span.ToArray().Any(value => value != 0));

        packet.Dispose();

        Assert.IsTrue(captured.Span.ToArray().All(value => value == 0));
        Assert.Throws<ObjectDisposedException>(() => _ = packet.Data);
        Assert.Throws<ObjectDisposedException>(() => _ = packet.Length);
        Array.Clear(userChars);
        Array.Clear(secretChars);
    }

    [TestMethod]
    public void WriteTo_CopiesPacketThenDisposeStillZeroesOriginalBackingMemory()
    {
        var userChars = new[] { 'u' };
        var secretChars = new[] { 'p' };
        var packet = ProjectLoginSecretPacket.Create(userChars, secretChars);
        var captured = packet.Data;
        using var stream = new MemoryStream();

        packet.WriteTo(stream);
        packet.Dispose();

        Assert.IsTrue(stream.Length > 13);
        Assert.IsTrue(captured.Span.ToArray().All(value => value == 0));
        Array.Clear(userChars);
        Array.Clear(secretChars);
    }

    [TestMethod]
    public void Create_RejectsEmptyOrOversizedCredentialParts()
    {
        Assert.Throws<ArgumentException>(() =>
            ProjectLoginSecretPacket.Create(ReadOnlyMemory<char>.Empty, new[] { 'x' }));
        Assert.Throws<ArgumentException>(() =>
            ProjectLoginSecretPacket.Create(new[] { 'u' }, ReadOnlyMemory<char>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectLoginSecretPacket.Create(new string('u', 321).ToCharArray(), new[] { 'x' }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectLoginSecretPacket.Create(new[] { 'u' }, new string('x', 1025).ToCharArray()));
    }
}