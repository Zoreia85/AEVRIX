namespace Aevrix.Remote.Security.Tests;

[TestClass]
public sealed class FileBackedDpopReplayStoreTests
{
    [TestMethod]
    public async Task RegistrationIsAtomicAndReplayFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-replay", Guid.NewGuid().ToString("N"));
        try
        {
            var digest = Enumerable.Repeat((byte)7, 32).ToArray();
            var first = new FileBackedDpopReplayStore(root);
            var accepted = await first.TryRegisterAsync(digest, TimeSpan.FromMinutes(2));
            var replay = await first.TryRegisterAsync(digest, TimeSpan.FromMinutes(2));
            Assert.IsTrue(accepted);
            Assert.IsFalse(replay);
            var file = Directory.GetFiles(root).Single();
            var expected = Convert.ToHexString(digest).ToLowerInvariant() + ".replay";
            Assert.AreEqual(expected, Path.GetFileName(file));
            Assert.IsTrue(long.TryParse(await File.ReadAllTextAsync(file), out _));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task InvalidInputFailsClosedWithoutStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-replay", Guid.NewGuid().ToString("N"));
        var store = new FileBackedDpopReplayStore(root);
        var badDigest = await store.TryRegisterAsync(new byte[31], TimeSpan.FromMinutes(1));
        var badTtl = await store.TryRegisterAsync(new byte[32], TimeSpan.Zero);
        Assert.IsFalse(badDigest);
        Assert.IsFalse(badTtl);
        Assert.IsFalse(Directory.Exists(root));
    }
}
