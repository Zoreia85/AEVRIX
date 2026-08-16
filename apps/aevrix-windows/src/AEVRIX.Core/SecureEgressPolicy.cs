using System.Net;

namespace Aevrix.Core;

public enum EgressMode
{
    Offline,
    ProtectedGateway,
    TargetOnly
}

public enum EgressHealth
{
    Unknown,
    Healthy,
    Degraded,
    Blocked
}

public sealed record EgressGateway(
    string Id,
    Uri HealthEndpoint,
    IReadOnlyList<IPAddress> ExpectedPublicAddresses,
    IReadOnlyList<string> AllowedDnsResolvers,
    bool RequireEncryptedDns,
    bool AllowFallbackToDirectInternet)
{
    public EgressGateway Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(HealthEndpoint);

        if (HealthEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Gateway health endpoint must use HTTPS.", nameof(HealthEndpoint));
        }

        if (AllowFallbackToDirectInternet)
        {
            throw new InvalidOperationException("AEVRIX protected egress must fail closed; direct-internet fallback is forbidden.");
        }

        if (ExpectedPublicAddresses.Count == 0)
        {
            throw new InvalidOperationException("Protected gateway must declare at least one expected public egress address.");
        }

        return this;
    }
}

public sealed record EgressPolicy(
    EgressMode Mode,
    bool HideHostNetworkAddressFromTarget,
    bool KillSwitchRequired,
    bool BlockDnsOutsideTunnel,
    bool BlockWebRtcDirectCandidates,
    bool BlockIpv6OutsideTunnel,
    IReadOnlyList<string> AllowedTargetHosts,
    EgressGateway? Gateway)
{
    public EgressPolicy Validate()
    {
        if (HideHostNetworkAddressFromTarget && Mode != EgressMode.ProtectedGateway)
        {
            throw new InvalidOperationException("Host-address privacy requires ProtectedGateway mode.");
        }

        if (Mode == EgressMode.ProtectedGateway)
        {
            if (!KillSwitchRequired || !BlockDnsOutsideTunnel)
            {
                throw new InvalidOperationException("ProtectedGateway requires kill-switch and DNS leak protection.");
            }

            Gateway?.Validate();
            if (Gateway is null)
            {
                throw new InvalidOperationException("ProtectedGateway requires a configured gateway.");
            }
        }

        if (Mode == EgressMode.TargetOnly && AllowedTargetHosts.Count == 0)
        {
            throw new InvalidOperationException("TargetOnly mode requires an explicit host allowlist.");
        }

        return this;
    }

    public static EgressPolicy Offline() => new(
        EgressMode.Offline,
        HideHostNetworkAddressFromTarget: false,
        KillSwitchRequired: true,
        BlockDnsOutsideTunnel: true,
        BlockWebRtcDirectCandidates: true,
        BlockIpv6OutsideTunnel: true,
        AllowedTargetHosts: [],
        Gateway: null);
}

public sealed record EgressObservation(
    EgressHealth Health,
    IPAddress? ObservedPublicAddress,
    string? ObservedDnsResolver,
    bool TunnelInterfaceAvailable,
    bool DirectDefaultRouteBlocked,
    bool WebRtcDirectCandidateObserved,
    bool DnsLeakObserved,
    DateTimeOffset CheckedAt,
    string? Detail = null);

public sealed record EgressDecision(bool AllowResearchTraffic, string Reason);

public static class EgressGuard
{
    public static EgressDecision Evaluate(EgressPolicy policy, EgressObservation observation)
    {
        policy.Validate();

        if (policy.Mode == EgressMode.Offline)
        {
            return new EgressDecision(false, "Project is configured for offline analysis.");
        }

        if (observation.Health is EgressHealth.Blocked or EgressHealth.Unknown)
        {
            return new EgressDecision(false, "Egress health is not verified.");
        }

        if (policy.Mode == EgressMode.ProtectedGateway)
        {
            var gateway = policy.Gateway!;
            if (!observation.TunnelInterfaceAvailable)
            {
                return new EgressDecision(false, "Protected tunnel is unavailable.");
            }

            if (!observation.DirectDefaultRouteBlocked)
            {
                return new EgressDecision(false, "Kill-switch is not proven; direct route remains available.");
            }

            if (observation.DnsLeakObserved || (policy.BlockDnsOutsideTunnel && string.IsNullOrWhiteSpace(observation.ObservedDnsResolver)))
            {
                return new EgressDecision(false, "DNS leak protection is not healthy.");
            }

            if (policy.BlockWebRtcDirectCandidates && observation.WebRtcDirectCandidateObserved)
            {
                return new EgressDecision(false, "WebRTC exposed a direct host-network candidate.");
            }

            if (observation.ObservedPublicAddress is null
                || !gateway.ExpectedPublicAddresses.Contains(observation.ObservedPublicAddress))
            {
                return new EgressDecision(false, "Observed public address does not match the configured gateway egress.");
            }
        }

        return new EgressDecision(true, "Research traffic passed the configured defensive egress policy.");
    }
}
