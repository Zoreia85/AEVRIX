using System.Diagnostics;
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

using var shutdownCts = new CancellationTokenSource();
var parentLifetimeTask = WatchSupervisingParentAsync(shutdownCts);
var runtime = new EngineHostRuntime(AevrixDataPaths.ForCurrentUser());

await using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

while (!shutdownCts.IsCancellationRequested)
{
    try
    {
        await pipe.WaitForConnectionAsync(shutdownCts.Token);
    }
    catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
    {
        break;
    }

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

await parentLifetimeTask.ConfigureAwait(false);

static async Task WatchSupervisingParentAsync(CancellationTokenSource shutdownCts)
{
    var rawParentPid = Environment.GetEnvironmentVariable(
        EngineProtocol.ParentProcessIdEnvironmentVariable);

    // Direct developer/test launches remain supported. A process started by EngineHostSupervisor
    // always receives this value and therefore becomes fail-closed to the parent lifetime.
    if (string.IsNullOrWhiteSpace(rawParentPid))
    {
        return;
    }

    if (!int.TryParse(rawParentPid, out var parentPid)
        || parentPid <= 0
        || parentPid == Environment.ProcessId)
    {
        shutdownCts.Cancel();
        return;
    }

    try
    {
        using var parent = Process.GetProcessById(parentPid);
        await parent.WaitForExitAsync().ConfigureAwait(false);
    }
    catch (ArgumentException)
    {
        // Parent already exited before the child obtained its process handle.
    }
    catch (InvalidOperationException)
    {
        // Treat an unobservable parent as dead rather than leaving an orphan runtime behind.
    }
    catch (System.ComponentModel.Win32Exception)
    {
        // Supervised EngineHost must not outlive a parent it cannot prove is still present.
    }
    finally
    {
        if (!shutdownCts.IsCancellationRequested)
        {
            shutdownCts.Cancel();
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
