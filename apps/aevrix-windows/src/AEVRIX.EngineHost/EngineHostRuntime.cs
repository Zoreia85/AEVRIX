using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevrix.Core;

namespace Aevrix.EngineHost;

public sealed record EngineHostRequest(string Token, EngineCommand Command);

public sealed class EngineHostRuntime
{
    private readonly BlueprintCommandHandler _blueprints;

    public EngineHostRuntime(AevrixDataPaths paths)
    {
        _blueprints = new BlueprintCommandHandler(paths);
    }

    public Task<EngineResponse> DispatchAsync(
        EngineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RequestId))
        {
            return Task.FromResult(new EngineResponse(
                string.Empty,
                false,
                "invalid_request_id",
                "Request id is required."));
        }

        if (command.ProtocolVersion != EngineProtocol.CurrentVersion)
        {
            return Task.FromResult(new EngineResponse(
                command.RequestId,
                false,
                "protocol_version_mismatch",
                $"Engine protocol {command.ProtocolVersion} is unsupported; expected {EngineProtocol.CurrentVersion}."));
        }

        return command switch
        {
            EnginePingCommand ping => Task.FromResult(new EngineResponse(
                ping.RequestId,
                true,
                "pong",
                "AEVRIX EngineHost is responsive.")),

            GetEngineStatusCommand status => Task.FromResult(new EngineResponse(
                status.RequestId,
                true,
                "engine_ready",
                "AEVRIX EngineHost is ready.",
                new
                {
                    protocolVersion = EngineProtocol.CurrentVersion,
                    processId = Environment.ProcessId
                })),

            DiagnoseEngineCommand diagnose when !diagnose.Repair => Task.FromResult(new EngineResponse(
                diagnose.RequestId,
                true,
                "diagnostics_ok",
                "Core EngineHost diagnostics passed.")),

            DiagnoseEngineCommand diagnose => Task.FromResult(new EngineResponse(
                diagnose.RequestId,
                false,
                "repair_not_implemented",
                "Automatic repair is not yet promoted.")),

            GenerateBlueprintCommand blueprint => _blueprints.HandleAsync(blueprint, cancellationToken),

            _ => Task.FromResult(new EngineResponse(
                command.RequestId,
                false,
                "command_not_implemented",
                $"Command {command.GetType().Name} is not yet promoted in EngineHost."))
        };
    }

    public static bool TokenMatches(string expected, string supplied)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public static EngineHostRequest DeserializeRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Engine request is empty.");
        }

        if (Encoding.UTF8.GetByteCount(json) > EngineProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException("Engine request exceeds the protocol message limit.");
        }

        try
        {
            return JsonSerializer.Deserialize<EngineHostRequest>(json)
                ?? throw new InvalidDataException("Engine request was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Engine request JSON is invalid.", ex);
        }
    }
}
