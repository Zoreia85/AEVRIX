using System.Text.Json;
using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class FirstRunAcceptanceStoreTests
{
    [TestMethod]
    public void MissingOrMalformedAcceptance_IsFailClosed()
    {
        using var temp = new TempDirectory();
        var store = new FirstRunAcceptanceStore(temp.Path);

        Assert.IsFalse(store.IsAccepted());

        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(store.AcceptancePath, "{not-json");

        Assert.IsFalse(store.IsAccepted());
    }

    [TestMethod]
    public void Accept_PersistsCurrentRevisionAndReloads()
    {
        using var temp = new TempDirectory();
        var acceptedAt = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var store = new FirstRunAcceptanceStore(temp.Path);

        var acceptance = store.Accept(acceptedAt);
        var reloaded = new FirstRunAcceptanceStore(temp.Path);

        Assert.AreEqual(FirstRunAcceptanceStore.CurrentSchemaVersion, acceptance.SchemaVersion);
        Assert.AreEqual(FirstRunAcceptanceStore.CurrentTermsRevision, acceptance.TermsRevision);
        Assert.AreEqual(acceptedAt, acceptance.AcceptedAtUtc);
        Assert.IsTrue(reloaded.IsAccepted());
    }

    [TestMethod]
    public void StaleRevision_IsRejected()
    {
        using var temp = new TempDirectory();
        var store = new FirstRunAcceptanceStore(temp.Path);
        Directory.CreateDirectory(temp.Path);

        var stale = new FirstRunAcceptance(
            FirstRunAcceptanceStore.CurrentSchemaVersion,
            "old-preview-terms",
            DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        File.WriteAllText(store.AcceptancePath, JsonSerializer.Serialize(stale));

        Assert.IsFalse(store.IsAccepted());
    }

    [TestMethod]
    public void RecordPresentation_PersistsCurrentTermsRevision()
    {
        using var temp = new TempDirectory();
        var presentedAt = DateTimeOffset.Parse("2026-08-17T12:01:00Z");
        var store = new FirstRunAcceptanceStore(temp.Path);

        var presentation = store.RecordPresentation(presentedAt);
        var saved = JsonSerializer.Deserialize<FirstRunPresentation>(File.ReadAllText(store.PresentationPath));

        Assert.IsNotNull(saved);
        Assert.AreEqual(FirstRunAcceptanceStore.CurrentSchemaVersion, presentation.SchemaVersion);
        Assert.AreEqual(FirstRunAcceptanceStore.CurrentTermsRevision, saved.TermsRevision);
        Assert.AreEqual(presentedAt, saved.PresentedAtUtc);
        Assert.IsFalse(store.IsAccepted());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-first-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
