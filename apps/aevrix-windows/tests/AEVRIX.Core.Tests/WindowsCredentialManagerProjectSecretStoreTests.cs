using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WindowsCredentialManagerProjectSecretStoreTests
{
    [TestMethod]
    public async Task SaveReadDelete_RoundTripsOnlyOnCurrentWindowsMachine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Credential Manager integration is Windows-only.");
            return;
        }

        var projectId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var secret = new ProjectCredentialSecret(
            "ci-user-" + Guid.NewGuid().ToString("N"),
            "ci-password-" + Guid.NewGuid().ToString("N"));
        var store = new WindowsCredentialManagerProjectSecretStore();

        try
        {
            await store.SaveAsync(projectId, credentialId, secret);
            var loaded = await store.ReadAsync(projectId, credentialId);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(secret.UserName, loaded.UserName);
            Assert.AreEqual(secret.Password, loaded.Password);
        }
        finally
        {
            await store.DeleteAsync(projectId, credentialId);
        }

        var deleted = await store.ReadAsync(projectId, credentialId);
        Assert.IsNull(deleted);
    }
}
