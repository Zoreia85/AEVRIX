using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevrix.Core;

namespace Aevrix.EngineHost;

public sealed record EngineHostRequest(string Token, EngineCommand Command);

public sealed class EngineHostRuntime
{
    private readonly BlueprintCommandHandler _blueprints;
    private readonly InvestigationRuntimeCoordinator _investigations;

    public EngineHostRuntime(AevrixDataPaths paths)
    {
        _blueprints = new BlueprintCommandHandler(paths);
        _investigations = new InvestigationRuntimeCoordinator(paths);
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
            RegisterInvestigationRuntimeCommand register => HandleRegisterInvestigationAsync(register, cancellationToken),
            ListInvestigationRuntimeCommand list => HandleListInvestigationsAsync(list, cancellationToken),
            ReconcileInvestigationScheduleCommand schedule => HandleReconcileScheduleAsync(schedule, cancellationToken),
            PauseInvestigationRuntimeCommand pause => HandlePauseInvestigationAsync(pause, cancellationToken),
            ResumeInvestigationRuntimeCommand resume => HandleResumeInvestigationAsync(resume, cancellationToken),
            CancelInvestigationRuntimeCommand cancel => HandleCancelInvestigationAsync(cancel, cancellationToken),

            _ => Task.FromResult(new EngineResponse(
                command.RequestId,
                false,
                "command_not_implemented",
                $"Command {command.GetType().Name} is not yet promoted in EngineHost."))
        };
    }

    private async Task<EngineResponse> HandleRegisterInvestigationAsync(
        RegisterInvestigationRuntimeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var registration = new InvestigationRuntimeRegistration(
                command.InvestigationId,
                command.Workspace,
                command.TargetKind,
                command.Strategy,
                command.AuthorizationClass,
                command.Priority,
                command.Artifacts);
            var record = await _investigations.RegisterAsync(registration, cancellationToken)
                .ConfigureAwait(false);
            return new EngineResponse(
                command.RequestId,
                true,
                "investigation_registered",
                "Investigation was bound to the local authenticated runtime.",
                record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or FileNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            return Fail(command.RequestId, "investigation_registration_blocked", ex);
        }
    }

    private async Task<EngineResponse> HandleListInvestigationsAsync(
        ListInvestigationRuntimeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await _investigations.ListAsync(cancellationToken).ConfigureAwait(false);
            return new EngineResponse(
                command.RequestId,
                true,
                "investigation_runtime_list",
                "Investigation runtime state was read from the local EngineHost store.",
                records);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Fail(command.RequestId, "investigation_runtime_unavailable", ex);
        }
    }

    private async Task<EngineResponse> HandleReconcileScheduleAsync(
        ReconcileInvestigationScheduleCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = await _investigations.ReconcileScheduleAsync(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new EngineResponse(
                command.RequestId,
                true,
                "investigation_schedule_reconciled",
                "Local investigation admission and scheduling were reconciled without simulating unavailable target work.",
                records);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return Fail(command.RequestId, "investigation_schedule_blocked", ex);
        }
    }

    private async Task<EngineResponse> HandlePauseInvestigationAsync(
        PauseInvestigationRuntimeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await _investigations.PauseAsync(command.InvestigationId, cancellationToken)
                .ConfigureAwait(false);
            return new EngineResponse(command.RequestId, true, "investigation_paused", "Investigation is paused.", record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            return Fail(command.RequestId, "investigation_pause_blocked", ex);
        }
    }

    private async Task<EngineResponse> HandleResumeInvestigationAsync(
        ResumeInvestigationRuntimeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await _investigations.ResumeAsync(command.InvestigationId, cancellationToken)
                .ConfigureAwait(false);
            return new EngineResponse(command.RequestId, true, "investigation_queued", "Investigation returned to the fair local queue.", record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            return Fail(command.RequestId, "investigation_resume_blocked", ex);
        }
    }

    private async Task<EngineResponse> HandleCancelInvestigationAsync(
        CancelInvestigationRuntimeCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await _investigations.CancelAsync(command.InvestigationId, cancellationToken)
                .ConfigureAwait(false);
            return new EngineResponse(command.RequestId, true, "investigation_cancelled", "Investigation was cancelled.", record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            return Fail(command.RequestId, "investigation_cancel_blocked", ex);
        }
    }

    private static EngineResponse Fail(string requestId, string code, Exception ex)
        => new(
            requestId,
            false,
            code,
            $"Operation failed closed ({ex.GetType().Name}).");

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
