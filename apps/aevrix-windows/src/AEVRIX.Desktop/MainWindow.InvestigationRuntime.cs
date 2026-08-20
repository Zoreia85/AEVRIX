using System.Text.Json;
using Aevrix.Core;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow
{
    internal async Task<InvestigationRuntimeRecord> RegisterInvestigationRuntimeAsync(
        InvestigationDraft draft,
        InvestigationPriority priority = InvestigationPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var command = new RegisterInvestigationRuntimeCommand(
            Guid.NewGuid().ToString("N"),
            draft.Id,
            draft.Workspace,
            draft.TargetKind,
            draft.Strategy,
            draft.AuthorizationClass,
            priority,
            draft.Artifacts);
        var response = await SendInvestigationRuntimeCommandAsync(command, cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord>(response);
    }

    internal async Task<IReadOnlyList<InvestigationRuntimeRecord>> ListInvestigationRuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendInvestigationRuntimeCommandAsync(
            new ListInvestigationRuntimeCommand(Guid.NewGuid().ToString("N")),
            cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord[]>(response);
    }

    internal async Task<IReadOnlyList<InvestigationRuntimeRecord>> ReconcileInvestigationRuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendInvestigationRuntimeCommandAsync(
            new ReconcileInvestigationScheduleCommand(Guid.NewGuid().ToString("N")),
            cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord[]>(response);
    }

    internal async Task<InvestigationRuntimeRecord> PauseInvestigationRuntimeAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendInvestigationRuntimeCommandAsync(
            new PauseInvestigationRuntimeCommand(Guid.NewGuid().ToString("N"), investigationId),
            cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord>(response);
    }

    internal async Task<InvestigationRuntimeRecord> ResumeInvestigationRuntimeAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendInvestigationRuntimeCommandAsync(
            new ResumeInvestigationRuntimeCommand(Guid.NewGuid().ToString("N"), investigationId),
            cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord>(response);
    }

    internal async Task<InvestigationRuntimeRecord> CancelInvestigationRuntimeAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendInvestigationRuntimeCommandAsync(
            new CancelInvestigationRuntimeCommand(Guid.NewGuid().ToString("N"), investigationId),
            cancellationToken);
        return ReadEngineData<InvestigationRuntimeRecord>(response);
    }

    private async Task<EngineResponse> SendInvestigationRuntimeCommandAsync(
        EngineCommand command,
        CancellationToken cancellationToken)
    {
        if (_isClosing)
        {
            throw new InvalidOperationException("Desktop is closing; investigation runtime commands are blocked.");
        }

        if (!_engineAuthenticated || _engineSupervisor is null || !_engineSupervisor.IsRunning)
        {
            await VerifyEngineHostAsync(restart: false);
        }

        if (!_engineAuthenticated || _engineSupervisor is null || !_engineSupervisor.IsRunning)
        {
            throw new InvalidOperationException("EngineHost authentication is not currently proven.");
        }

        var response = await _engineSupervisor.SendAsync(command, cancellationToken);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"EngineHost rejected {command.GetType().Name}: {response.Code} — {response.Message}");
        }
        return response;
    }

    private static T ReadEngineData<T>(EngineResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Data is null)
        {
            throw new InvalidDataException($"EngineHost response '{response.Code}' did not include runtime data.");
        }

        try
        {
            if (response.Data is JsonElement element)
            {
                return element.Deserialize<T>()
                    ?? throw new InvalidDataException($"EngineHost response '{response.Code}' data was null.");
            }

            var json = JsonSerializer.Serialize(response.Data);
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidDataException($"EngineHost response '{response.Code}' data was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"EngineHost response '{response.Code}' contained invalid investigation runtime data.",
                ex);
        }
    }
}
