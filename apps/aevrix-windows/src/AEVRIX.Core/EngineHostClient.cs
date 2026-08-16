using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed class EngineHostClient
{
    private sealed record RequestEnvelope(string Token, EngineCommand Command);

    private readonly string _pipeName;
    private readonly string _token;
    private readonly TimeSpan _connectTimeout;

    public EngineHostClient(string pipeName, string token, TimeSpan? connectTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName)
            || !pipeName.StartsWith(EngineProtocol.PipeNamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Engine pipe name is invalid.", nameof(pipeName));
        }

        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32)
        {
            throw new ArgumentException("Engine session token must contain at least 32 UTF-8 bytes.", nameof(token));
        }

        _pipeName = pipeName;
        _token = token;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);

        if (_connectTimeout <= TimeSpan.Zero || _connectTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }
    }

    public async Task<EngineResponse> SendAsync(
        EngineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.RequestId))
        {
            throw new ArgumentException("Engine command request id is required.", nameof(command));
        }

        using var timeoutCts = new CancellationTokenSource(_connectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new RequestEnvelope(_token, command)) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > EngineProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("Engine request exceeds the protocol message limit.");
        }

        await pipe.WriteAsync(bytes, linkedCts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(linkedCts.Token).ConfigureAwait(false);

        var responseJson = await ReadBoundedLineAsync(pipe, linkedCts.Token).ConfigureAwait(false);
        EngineResponse response;
        try
        {
            response = JsonSerializer.Deserialize<EngineResponse>(responseJson)
                ?? throw new InvalidDataException("Engine response was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Engine response JSON is invalid.", ex);
        }

        if (response.ProtocolVersion != EngineProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Engine response protocol {response.ProtocolVersion} is unsupported; expected {EngineProtocol.CurrentVersion}.");
        }

        if (!string.Equals(response.RequestId, command.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Engine response request id does not match the command request id.");
        }

        return response;
    }

    private static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var collected = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("EngineHost closed the pipe before returning a response.");
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;

            if (collected.Length + count > EngineProtocol.MaxMessageBytes)
            {
                throw new InvalidDataException("Engine response exceeds the protocol message limit.");
            }

            collected.Write(buffer, 0, count);
            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(collected.GetBuffer(), 0, checked((int)collected.Length));
            }
        }
    }
}
