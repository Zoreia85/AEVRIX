using System.Globalization;

namespace Aevrix.Remote.Security;

public sealed class FileBackedDpopReplayStore(string root, TimeProvider? clock = null) : IDpopReplayStore
{
    private readonly string _root = Path.GetFullPath(
        string.IsNullOrWhiteSpace(root) ? throw new ArgumentException("Replay root required.", nameof(root)) : root);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async ValueTask<bool> TryRegisterAsync(
        ReadOnlyMemory<byte> jtiSha256, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (jtiSha256.Length != 32 || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(10))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, Convert.ToHexString(jtiSha256.Span).ToLowerInvariant() + ".replay");
        var now = _clock.GetUtcNow();
        var expiry = now.Add(ttl).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        try
        {
            Directory.CreateDirectory(_root);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    await using var writer = new StreamWriter(file);
                    await writer.WriteAsync(expiry.AsMemory(), cancellationToken);
                    await writer.FlushAsync(cancellationToken);
                    return true;
                }
                catch (IOException) when (File.Exists(path))
                {
                    string text;
                    try { text = await File.ReadAllTextAsync(path, cancellationToken); }
                    catch (IOException) { return false; }
                    catch (UnauthorizedAccessException) { return false; }

                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
                        return false;
                    DateTimeOffset existingExpiry;
                    try { existingExpiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
                    catch (ArgumentOutOfRangeException) { return false; }
                    if (existingExpiry > now)
                        return false;
                    try { File.Delete(path); }
                    catch (IOException) { return false; }
                    catch (UnauthorizedAccessException) { return false; }
                }
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return false;
    }
}
