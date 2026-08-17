using System.Reflection;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AiProviderBudgetManagerTests
{
    private static readonly Guid ProjectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public void LocalOnly_PermitsLocalAndRejectsRemoteBeforeInvocation()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.LocalOnly));

        var local = manager.Reserve(Estimate(ProjectA, "local-1", "ollama", AiProviderLocation.Local));
        var remote = manager.Reserve(Estimate(ProjectA, "remote-1", "premium", AiProviderLocation.Remote));

        Assert.IsTrue(local.Allowed);
        Assert.IsNotNull(local.Reservation);
        Assert.IsFalse(remote.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.RemoteProviderDenied, remote.DenialReason);
    }

    [TestMethod]
    public void Reserve_DeniesSpendAndTokenOverrunsBeforeProviderInvocation()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.Balanced, spend: 1_000, calls: 2, input: 100, output: 50));

        var spend = manager.Reserve(Estimate(ProjectA, "spend", "remote", AiProviderLocation.Remote, cost: 1_001, input: 10, output: 10));
        var input = manager.Reserve(Estimate(ProjectA, "input", "remote", AiProviderLocation.Remote, cost: 100, input: 101, output: 10));
        var output = manager.Reserve(Estimate(ProjectA, "output", "remote", AiProviderLocation.Remote, cost: 100, input: 10, output: 51));

        Assert.AreEqual(AiBudgetDenialReason.SpendLimitExceeded, spend.DenialReason);
        Assert.AreEqual(AiBudgetDenialReason.InputTokenLimitExceeded, input.DenialReason);
        Assert.AreEqual(AiBudgetDenialReason.OutputTokenLimitExceeded, output.DenialReason);
        Assert.AreEqual(0L, manager.GetSnapshot(ProjectA).ReservedSpendMicros);
    }

    [TestMethod]
    public void Budgets_AreStrictlyIsolatedByProject()
    {
        var manager = Manager(
            Policy(ProjectA, AiProviderBudgetProfile.Balanced, spend: 1_000, calls: 1, input: 100, output: 100),
            Policy(ProjectB, AiProviderBudgetProfile.Balanced, spend: 5_000, calls: 5, input: 500, output: 500));

        var reservation = manager.Reserve(Estimate(ProjectA, "a-1", "remote", AiProviderLocation.Remote, cost: 900, input: 80, output: 70));
        Assert.IsTrue(reservation.Allowed);

        var a = manager.GetSnapshot(ProjectA);
        var b = manager.GetSnapshot(ProjectB);

        Assert.AreEqual(100L, a.RemainingSpendMicros);
        Assert.AreEqual(5_000L, b.RemainingSpendMicros);
        Assert.AreEqual(0, a.RemainingMeteredCalls);
        Assert.AreEqual(5, b.RemainingMeteredCalls);
    }

    [TestMethod]
    public void Complete_IsIdempotentAndCannotDoubleCharge()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.Balanced));
        var estimate = Estimate(ProjectA, "call-1", "premium", AiProviderLocation.Remote, cost: 700, input: 60, output: 40);
        Assert.IsTrue(manager.Reserve(estimate).Allowed);

        var receipt = new AiProviderUsageReceipt(ProjectA, "call-1", "premium", "USD", 650, 55, 35);
        var first = manager.Complete(receipt);
        var duplicate = manager.Complete(receipt);

        Assert.AreEqual(650L, first.CommittedSpendMicros);
        Assert.AreEqual(first, duplicate);
        Assert.AreEqual(1, duplicate.CommittedMeteredCalls);

        var repeated = manager.Reserve(estimate);
        Assert.IsFalse(repeated.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.RequestAlreadyCompleted, repeated.DenialReason);
    }

    [TestMethod]
    public void Complete_FailsClosedWhenProviderUsageExceedsReservation()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.Balanced));
        manager.Reserve(Estimate(ProjectA, "call-1", "premium", AiProviderLocation.Remote, cost: 500, input: 50, output: 40));

        Assert.Throws<InvalidDataException>(() => manager.Complete(
            new AiProviderUsageReceipt(ProjectA, "call-1", "premium", "USD", 501, 50, 40)));

        var snapshot = manager.GetSnapshot(ProjectA);
        Assert.AreEqual(500L, snapshot.ReservedSpendMicros);
        Assert.AreEqual(0L, snapshot.CommittedSpendMicros);
    }

    [TestMethod]
    public void Cancel_ReleasesReservedCapacityWithoutCreatingSpend()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.Balanced, spend: 500, calls: 1, input: 50, output: 50));
        manager.Reserve(Estimate(ProjectA, "call-1", "premium", AiProviderLocation.Remote, cost: 500, input: 50, output: 50));

        var blocked = manager.Reserve(Estimate(ProjectA, "call-2", "premium", AiProviderLocation.Remote, cost: 1, input: 1, output: 1));
        Assert.IsFalse(blocked.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.MeteredCallLimitExceeded, blocked.DenialReason);

        var released = manager.Cancel(ProjectA, "call-1");
        Assert.AreEqual(500L, released.RemainingSpendMicros);
        Assert.AreEqual(1, released.RemainingMeteredCalls);
        Assert.AreEqual(0L, released.CommittedSpendMicros);
    }

    [TestMethod]
    public void ReservationId_IsIdempotentButCannotBeRebound()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.Balanced));
        var estimate = Estimate(ProjectA, "same-request", "provider-a", AiProviderLocation.Remote, cost: 100, input: 10, output: 10);

        var first = manager.Reserve(estimate);
        var duplicate = manager.Reserve(estimate);
        var conflict = manager.Reserve(estimate with { ProviderId = "provider-b" });

        Assert.IsTrue(first.Allowed);
        Assert.IsTrue(duplicate.Allowed);
        Assert.AreEqual(first.Reservation, duplicate.Reservation);
        Assert.IsFalse(conflict.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.RequestIdConflict, conflict.DenialReason);
        Assert.AreEqual(100L, manager.GetSnapshot(ProjectA).ReservedSpendMicros);
    }

    [TestMethod]
    public void InvalidNegativeUsageAndInvalidLocalMetering_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Policy(ProjectA, AiProviderBudgetProfile.Custom, spend: -1).Validate());

        Assert.Throws<ArgumentException>(() =>
            new AiProviderCallEstimate(ProjectA, "bad-local", "local", AiProviderLocation.Local, AiProviderBillingMode.ExternalMetered, "USD", 1, 1, 1, 10).Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiProviderUsageReceipt(ProjectA, "receipt", "provider", "USD", -1, 0, 0).Validate());
    }

    [TestMethod]
    public void Contracts_DoNotExposeCredentialOrAuthorizationMaterial()
    {
        var contractTypes = new[]
        {
            typeof(AiProjectBudgetPolicy),
            typeof(AiProviderCallEstimate),
            typeof(AiProviderBudgetReservation),
            typeof(AiProviderUsageReceipt),
            typeof(AiProjectBudgetSnapshot)
        };
        var forbiddenNames = new[] { "ApiKey", "Secret", "Credential", "Authorization", "Bearer", "Password" };

        foreach (var type in contractTypes)
        {
            var propertyNames = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name).ToArray();
            foreach (var forbidden in forbiddenNames)
            {
                Assert.IsFalse(propertyNames.Any(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name} must not expose credential-bearing field '{forbidden}'.");
            }
        }
    }

    [TestMethod]
    public void AllowedProviderAndLatencyPolicies_AreEnforced()
    {
        var policy = Policy(ProjectA, AiProviderBudgetProfile.Economy) with
        {
            AllowedProviderIds = ["approved-provider"],
            MaximumEstimatedLatencyMilliseconds = 500
        };
        var manager = Manager(policy);

        var providerDenied = manager.Reserve(Estimate(ProjectA, "provider", "not-approved", AiProviderLocation.Remote));
        var latencyDenied = manager.Reserve(Estimate(ProjectA, "latency", "approved-provider", AiProviderLocation.Remote) with
        {
            EstimatedLatencyMilliseconds = 501
        });

        Assert.AreEqual(AiBudgetDenialReason.ProviderNotAllowed, providerDenied.DenialReason);
        Assert.AreEqual(AiBudgetDenialReason.EstimatedLatencyLimitExceeded, latencyDenied.DenialReason);
    }

    [TestMethod]
    public void MeteredCallReservations_ConsumeCapacityBeforeParallelProviderCallsStart()
    {
        var manager = Manager(Policy(ProjectA, AiProviderBudgetProfile.MaximumQuality, spend: 10_000, calls: 2, input: 1_000, output: 1_000));

        Assert.IsTrue(manager.Reserve(Estimate(ProjectA, "parallel-1", "provider-a", AiProviderLocation.Remote, cost: 100)).Allowed);
        Assert.IsTrue(manager.Reserve(Estimate(ProjectA, "parallel-2", "provider-b", AiProviderLocation.Remote, cost: 100)).Allowed);
        var third = manager.Reserve(Estimate(ProjectA, "parallel-3", "provider-c", AiProviderLocation.Remote, cost: 100));

        Assert.IsFalse(third.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.MeteredCallLimitExceeded, third.DenialReason);
        Assert.AreEqual(2, manager.GetSnapshot(ProjectA).ReservedMeteredCalls);
    }

    private static AiProviderBudgetManager Manager(params AiProjectBudgetPolicy[] policies) =>
        new(policies, new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)));

    private static AiProjectBudgetPolicy Policy(
        Guid projectId,
        AiProviderBudgetProfile profile,
        long spend = 10_000,
        int calls = 10,
        long input = 10_000,
        long output = 10_000) =>
        new(projectId, profile, "USD", spend, calls, input, output);

    private static AiProviderCallEstimate Estimate(
        Guid projectId,
        string requestId,
        string providerId,
        AiProviderLocation location,
        long cost = 0,
        long input = 10,
        long output = 10) =>
        new(
            projectId,
            requestId,
            providerId,
            location,
            location == AiProviderLocation.Remote ? AiProviderBillingMode.ExternalMetered : AiProviderBillingMode.None,
            "USD",
            cost,
            input,
            output,
            100);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
