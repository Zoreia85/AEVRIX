using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WorkspaceEnvelopeEncryptionTests
{
    [TestMethod]
    public void EncryptDecrypt_RoundTripsWithinSameScopeAndPurpose()
    {
        var crypto = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-a", "enc-a"));
        var masterKey = SHA256.HashData(Encoding.UTF8.GetBytes("test-only-master-key-material"));
        var plaintext = Encoding.UTF8.GetBytes("private evidence payload");

        var envelope = crypto.Encrypt(plaintext, masterKey, "evidence");
        var restored = crypto.Decrypt(envelope, masterKey, "evidence");

        CollectionAssert.AreEqual(plaintext, restored);
        CollectionAssert.AreNotEqual(plaintext, envelope.Ciphertext);
        Assert.AreEqual(64, envelope.ScopeBindingSha256.Length);
    }

    [TestMethod]
    public void Decrypt_RejectsDifferentWorkspaceUserOrEncryptionContext()
    {
        var origin = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-a", "enc-a"));
        var differentWorkspace = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-b", "user-a", "enc-a"));
        var differentUser = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-b", "enc-a"));
        var differentEncryptionContext = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-a", "enc-b"));
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var envelope = origin.Encrypt(Encoding.UTF8.GetBytes("payload"), masterKey, "evidence");

        Assert.ThrowsExactly<CryptographicException>(() => differentWorkspace.Decrypt(envelope, masterKey, "evidence"));
        Assert.ThrowsExactly<CryptographicException>(() => differentUser.Decrypt(envelope, masterKey, "evidence"));
        Assert.ThrowsExactly<CryptographicException>(() => differentEncryptionContext.Decrypt(envelope, masterKey, "evidence"));
    }

    [TestMethod]
    public void Decrypt_RejectsPurposeMismatchAndTampering()
    {
        var crypto = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-a", "enc-a"));
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var envelope = crypto.Encrypt(Encoding.UTF8.GetBytes("payload"), masterKey, "evidence");

        Assert.ThrowsExactly<CryptographicException>(() => crypto.Decrypt(envelope, masterKey, "blueprint"));

        envelope.Ciphertext[0] ^= 0x01;
        Assert.ThrowsExactly<CryptographicException>(() => crypto.Decrypt(envelope, masterKey, "evidence"));
    }

    [TestMethod]
    public void Encrypt_RejectsShortMasterKey()
    {
        var crypto = new WorkspaceEnvelopeEncryption(new WorkspaceScope("workspace-a", "user-a", "enc-a"));

        Assert.ThrowsExactly<ArgumentException>(() =>
            crypto.Encrypt(Encoding.UTF8.GetBytes("payload"), new byte[31], "evidence"));
    }
}
