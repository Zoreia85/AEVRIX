using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class OperationalActivityJournalTests
{
    [TestMethod]
    public void Append_ReturnsNewestEntriesFirstAndNormalizesWhitespace()
    {
        var journal = new OperationalActivityJournal(capacity: 4);
        var firstTimestamp = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var secondTimestamp = firstTimestamp.AddMinutes(1);

        journal.Append(
            OperationalActivityLevel.Informational,
            " Desktop ",
            " Sessão   iniciada ",
            " Shell\ncarregado com segurança. ",
            firstTimestamp);

        journal.Append(
            OperationalActivityLevel.Success,
            "EngineHost",
            "Ping confirmado",
            "Sessão autenticada.",
            secondTimestamp);

        var entries = journal.Snapshot();

        Assert.HasCount(2, entries);
        Assert.AreEqual(secondTimestamp, entries[0].TimestampUtc);
        Assert.AreEqual("Ping confirmado", entries[0].Title);
        Assert.AreEqual("Desktop", entries[1].Source);
        Assert.AreEqual("Sessão iniciada", entries[1].Title);
        Assert.AreEqual("Shell carregado com segurança.", entries[1].Detail);
    }

    [TestMethod]
    public void Append_DropsOldestEntryWhenCapacityIsReached()
    {
        var journal = new OperationalActivityJournal(capacity: 2);

        journal.Append(OperationalActivityLevel.Informational, "Desktop", "Um", "Primeiro evento.");
        journal.Append(OperationalActivityLevel.Warning, "Desktop", "Dois", "Segundo evento.");
        journal.Append(OperationalActivityLevel.Error, "Desktop", "Três", "Terceiro evento.");

        var entries = journal.Snapshot();

        Assert.HasCount(2, entries);
        Assert.AreEqual("Três", entries[0].Title);
        Assert.AreEqual("Dois", entries[1].Title);
    }

    [TestMethod]
    public void Append_RejectsBlankUserFacingFields()
    {
        var journal = new OperationalActivityJournal();

        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.Append(OperationalActivityLevel.Warning, " ", "Título", "Detalhe"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.Append(OperationalActivityLevel.Warning, "Desktop", " ", "Detalhe"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.Append(OperationalActivityLevel.Warning, "Desktop", "Título", " "));
    }

    [TestMethod]
    public void Constructor_RejectsUnboundedCapacity()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OperationalActivityJournal(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OperationalActivityJournal(1001));
    }
}
