namespace Aevrix.Core;

public enum OperationalActivityLevel
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed record OperationalActivityEntry(
    DateTimeOffset TimestampUtc,
    OperationalActivityLevel Level,
    string Source,
    string Title,
    string Detail);

/// <summary>
/// Bounded, in-memory journal for product-facing operational activity.
/// Entries are intentionally concise and must never be treated as the canonical proof ledger.
/// The caller is responsible for passing privacy-safe summaries rather than raw payloads or secrets.
/// </summary>
public sealed class OperationalActivityJournal
{
    private const int MaximumFieldLength = 320;
    private readonly object _gate = new();
    private readonly LinkedList<OperationalActivityEntry> _entries = new();

    public OperationalActivityJournal(int capacity = 100)
    {
        if (capacity is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be between 1 and 1000 entries.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public OperationalActivityEntry Append(
        OperationalActivityLevel level,
        string source,
        string title,
        string detail,
        DateTimeOffset? timestamp = null)
    {
        var entry = new OperationalActivityEntry(
            (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            level,
            NormalizeRequired(source, nameof(source)),
            NormalizeRequired(title, nameof(title)),
            NormalizeRequired(detail, nameof(detail)));

        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        return entry;
    }

    public IReadOnlyList<OperationalActivityEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= MaximumFieldLength
            ? normalized
            : normalized[..MaximumFieldLength];
    }
}
