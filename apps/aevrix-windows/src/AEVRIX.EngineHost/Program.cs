using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aevrix.Core;
using Aevrix.EngineHost;

var pipeName = Environment.GetEnvironmentVariable(EngineProtocol.PipeEnvironmentVariable);
var token = Environment.GetEnvironmentVariable(EngineProtocol.TokenEnvironmentVariable);

if (string.IsNullOrWhiteSpace(pipeName)
    || !pipeName.StartsWith(EngineProtocol.PipeNamePrefix, StringComparison.Ordinal)
    || string.IsNullOrWhiteSpace(token)
    || Encoding.UTF8.GetByteCount(token) < 32)
{
    Environment.ExitCode = 64;
    return;
}

var runtime = new EngineHostRuntime(AevrixDataPaths.ForCurrentUser());

await using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

while (true)
{
    await pipe.WaitForConnectionAsync();

    try
    {
        EngineResponse response;
        try
        {
            var requestJson = await ReadBoundedLineAsync(pipe);
            var request = EngineHostRuntime.DeserializeRequest(requestJson);

            if (!EngineHostRuntime.TokenMatches(token, request.Token))
            {
                response = new EngineResponse(
                    request.Command?.RequestId ?? string.Empty,
                    false,
                    "unauthorized",
                    "EngineHost authentication failed.");
            }
            else if (request.Command is null)
            {
                response = new EngineResponse(
                    string.Empty,
                    false,
                    "invalid_command",
                    "Engine command is required.");
            }
            else
            {
                response = await runtime.DispatchAsync(request.Command);
            }
        }
        catch (InvalidDataException ex)
        {
            response = new EngineResponse(string.Empty, false, "invalid_request", ex.Message);
        }
        catch
        {
            response = new EngineResponse(
                string.Empty,
                false,
                "engine_error",
                "EngineHost rejected the request.");
        }

        var payload = JsonSerializer.Serialize(response) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }
    catch (IOException)
    {
        // A client may disconnect after sending a request or while receiving the response.
        // The server keeps the same pipe instance and proceeds to the next bounded connection.
    }
    finally
    {
        if (pipe.IsConnected)
        {
            pipe.Disconnect();
        }
    }
}

static async Task<string> ReadBoundedLineAsync(Stream stream)
{
    var buffer = new byte[4096];
    using var payload = new MemoryStream();

    while (true)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }

        var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
        var count = newline >= 0 ? newline : read;

        if (payload.Length + count > EngineProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("Engine request exceeds the protocol message limit.");
        }

        payload.Write(buffer, 0, count);
        if (newline >= 0)
        {
            break;
        }
    }

    return Encoding.UTF8.GetString(payload.ToArray()).TrimEnd('\r');
}
