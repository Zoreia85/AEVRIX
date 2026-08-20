namespace Aevrix.Core;

public enum InvestigationPriority
{
    Low = 10,
    Normal = 50,
    High = 80,
    Urgent = 100
}

public sealed record InvestigationResourceBudget(
    int CpuWeight,
    long MemoryBytes,
    int MaxParallelAgentPackages)
{
    public static InvestigationResourceBudget ConservativeDefault(LocalCapacityRecommendation capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        var slots = Math.Clamp(
            capacity.RecommendedConcurrentInvestigations,
            1,
            LocalCapacityRecommendation.ProductMaximumConcurrentInvestigations);
        var memoryPerInvestigation = Math.Max(
            1024L * 1024 * 1024,
            capacity.AvailableMemoryBytes / Math.Max(2, slots + 1));
        return new InvestigationResourceBudget(
            CpuWeight: Math.Max(1, 100 / slots),
            MemoryBytes: memoryPerInvestigation,
            MaxParallelAgentPackages: Math.Clamp(capacity.LogicalProcessors / Math.Max(2, slots * 2), 1, 4));
    }
}

public sealed record InvestigationScheduleRequest(
    Guid InvestigationId,
    InvestigationPriority Priority,
    DateTimeOffset EnqueuedAtUtc,
    InvestigationRunState CurrentState);

public sealed record InvestigationScheduleDecision(
    Guid InvestigationId,
    InvestigationRunState NextState,
    InvestigationResourceBudget Budget,
    int QueuePosition,
    string Reason);

public static class InvestigationScheduler
{
    private const double AgingMinutesPerPriorityPoint = 2.0;
    private const double MaximumAgingPriorityPoints = 150.0;

    public static IReadOnlyList<InvestigationScheduleDecision> Plan(
        IEnumerable<InvestigationScheduleRequest> requests,
        LocalCapacityRecommendation capacity,
        DateTimeOffset? planningAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(capacity);

        var now = planningAtUtc ?? DateTimeOffset.UtcNow;
        var budget = InvestigationResourceBudget.ConservativeDefault(capacity);
        var normalized = requests
            .Where(request => request.CurrentState is not (
                InvestigationRunState.Completed or
                InvestigationRunState.Cancelled or
                InvestigationRunState.Failed))
            .OrderByDescending(request => request.CurrentState == InvestigationRunState.Running)
            .ThenByDescending(request => ComputeFairSchedulingScore(request, now))
            .ThenBy(request => request.EnqueuedAtUtc)
            .ToArray();

        var maxRunning = Math.Clamp(
            capacity.RecommendedConcurrentInvestigations,
            1,
            LocalCapacityRecommendation.ProductMaximumConcurrentInvestigations);
        var decisions = new List<InvestigationScheduleDecision>(normalized.Length);
        var runningCount = 0;
        var queuePosition = 0;

        foreach (var request in normalized)
        {
            if (request.CurrentState == InvestigationRunState.Running)
            {
                runningCount++;
                decisions.Add(new InvestigationScheduleDecision(
                    request.InvestigationId,
                    InvestigationRunState.Running,
                    budget,
                    0,
                    runningCount <= maxRunning
                        ? "Execução mantida dentro do orçamento atual da estação."
                        : "Execução já iniciada foi preservada após redução de capacidade. Nenhum novo trabalho será admitido até o número ativo retornar ao orçamento atual."));
                continue;
            }

            if (request.CurrentState is InvestigationRunState.Paused or InvestigationRunState.Blocked)
            {
                decisions.Add(new InvestigationScheduleDecision(
                    request.InvestigationId,
                    request.CurrentState,
                    budget,
                    0,
                    request.CurrentState == InvestigationRunState.Paused
                        ? "A investigação permanece pausada por decisão explícita."
                        : "A investigação permanece bloqueada até que o gate pendente seja resolvido."));
                continue;
            }

            if (runningCount < maxRunning)
            {
                runningCount++;
                var aged = ComputeAgingPriorityPoints(request, now);
                decisions.Add(new InvestigationScheduleDecision(
                    request.InvestigationId,
                    InvestigationRunState.Running,
                    budget,
                    0,
                    aged > 0
                        ? "Slot de execução concedido pela fila justa com envelhecimento de prioridade; trabalho antigo não é deixado indefinidamente para trás."
                        : "Slot de execução disponível segundo a capacidade conservadora da estação."));
                continue;
            }

            queuePosition++;
            decisions.Add(new InvestigationScheduleDecision(
                request.InvestigationId,
                InvestigationRunState.Queued,
                budget,
                queuePosition,
                runningCount > maxRunning
                    ? "A estação está temporariamente acima da nova capacidade por preservar trabalho já em execução; nenhuma nova investigação entra até o total ativo cair dentro do orçamento."
                    : "Capacidade simultânea atingida; a investigação permanece em fila justa. O tempo de espera aumenta gradualmente sua prioridade efetiva para evitar starvation."));
        }

        return decisions;
    }

    internal static double ComputeFairSchedulingScore(
        InvestigationScheduleRequest request,
        DateTimeOffset planningAtUtc)
        => (int)request.Priority + ComputeAgingPriorityPoints(request, planningAtUtc);

    private static double ComputeAgingPriorityPoints(
        InvestigationScheduleRequest request,
        DateTimeOffset planningAtUtc)
    {
        var waiting = planningAtUtc - request.EnqueuedAtUtc;
        if (waiting <= TimeSpan.Zero || request.CurrentState == InvestigationRunState.Running)
        {
            return 0;
        }

        return Math.Min(
            MaximumAgingPriorityPoints,
            waiting.TotalMinutes / AgingMinutesPerPriorityPoint);
    }
}

public static class InvestigationStateMachine
{
    public static bool CanTransition(InvestigationRunState from, InvestigationRunState to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            InvestigationRunState.Draft => to is InvestigationRunState.Ready or InvestigationRunState.Blocked or InvestigationRunState.Cancelled,
            InvestigationRunState.Ready => to is InvestigationRunState.Queued or InvestigationRunState.Running or InvestigationRunState.Blocked or InvestigationRunState.Cancelled,
            InvestigationRunState.Queued => to is InvestigationRunState.Running or InvestigationRunState.Paused or InvestigationRunState.Blocked or InvestigationRunState.Cancelled,
            InvestigationRunState.Running => to is InvestigationRunState.Paused or InvestigationRunState.Blocked or InvestigationRunState.Failed or InvestigationRunState.Completed or InvestigationRunState.Cancelled,
            InvestigationRunState.Paused => to is InvestigationRunState.Queued or InvestigationRunState.Running or InvestigationRunState.Blocked or InvestigationRunState.Cancelled,
            InvestigationRunState.Blocked => to is InvestigationRunState.Ready or InvestigationRunState.Queued or InvestigationRunState.Failed or InvestigationRunState.Cancelled,
            InvestigationRunState.Failed => to is InvestigationRunState.Ready or InvestigationRunState.Cancelled,
            InvestigationRunState.Completed => false,
            InvestigationRunState.Cancelled => false,
            _ => false
        };
    }

    public static void RequireTransition(InvestigationRunState from, InvestigationRunState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Investigation state transition {from} -> {to} is not allowed.");
        }
    }
}
