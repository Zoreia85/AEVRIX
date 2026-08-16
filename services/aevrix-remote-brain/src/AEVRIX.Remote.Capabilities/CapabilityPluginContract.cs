namespace Aevrix.Remote.Capabilities;

public enum TargetAuthorizationScope
{
    ThirdPartyCleanRoom,
    OwnedSystem,
    ExplicitlyAuthorizedSystem
}

public enum AnalysisTechnique
{
    Static,
    Dynamic,
    RuntimeInstrumentation
}

public enum DataExposureClass
{
    None,
    MetadataOnly,
    MinimizedContent
}

public sealed record CapabilityPluginContract(
    string PluginId,
    string Capability,
    CapabilitySource Source,
    CapabilityApprovalState Approval,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> OperatingSystems,
    IReadOnlyList<AnalysisTechnique> Techniques,
    bool RequiresWorkspaceBinding,
    bool RequiresSubjectBinding,
    bool AllowsOutboundNetwork,
    bool AllowsSecretMaterial,
    DataExposureClass MaximumDataExposure)
{
    public CapabilityPluginContract Validate()
    {
        McpServerDescriptor.ValidateId(PluginId, nameof(PluginId));
        McpServerDescriptor.ValidateId(Capability, nameof(Capability));
        Source.Validate();
        ArgumentNullException.ThrowIfNull(Domains);
        ArgumentNullException.ThrowIfNull(Languages);
        ArgumentNullException.ThrowIfNull(Formats);
        ArgumentNullException.ThrowIfNull(OperatingSystems);
        ArgumentNullException.ThrowIfNull(Techniques);

        if (Domains.Count is 0 or > 128
            || Languages.Count > 128
            || Formats.Count > 128
            || OperatingSystems.Count > 64
            || Techniques.Count is 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(Domains), "Plugin contract exceeds bounded declaration limits.");
        }

        ValidateTokens(Domains, nameof(Domains));
        ValidateTokens(Languages, nameof(Languages));
        ValidateTokens(Formats, nameof(Formats));
        ValidateTokens(OperatingSystems, nameof(OperatingSystems));

        if (!RequiresWorkspaceBinding || !RequiresSubjectBinding)
        {
            throw new InvalidOperationException("AEVRIX plugins must be bound to both workspace and subject identity.");
        }

        if (AllowsSecretMaterial && MaximumDataExposure == DataExposureClass.None)
        {
            throw new InvalidOperationException("A plugin that accepts secret material cannot declare zero data exposure.");
        }

        AevrixCapabilityPolicy.EnsureAllowed(new[] { Capability });
        return this;
    }

    public bool CanRegister()
    {
        try
        {
            Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        return Approval == CapabilityApprovalState.Approved;
    }

    private static void ValidateTokens(IReadOnlyList<string> values, string parameterName)
    {
        if (values.Any(value => string.IsNullOrWhiteSpace(value)
            || value.Length > 120
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '+' or '#'))))
        {
            throw new ArgumentException("Plugin declarations must contain bounded identifier tokens only.", parameterName);
        }
    }
}

public sealed record CapabilityExecutionContext(
    string WorkspaceId,
    string SubjectId,
    TargetAuthorizationScope AuthorizationScope,
    AnalysisTechnique Technique,
    bool OutboundNetworkRequested,
    bool SecretMaterialRequested,
    DataExposureClass RequestedDataExposure)
{
    public CapabilityExecutionContext Validate()
    {
        McpServerDescriptor.ValidateId(WorkspaceId, nameof(WorkspaceId));
        McpServerDescriptor.ValidateId(SubjectId, nameof(SubjectId));
        return this;
    }
}

public sealed record CapabilityAdmissionDecision(bool Allowed, string Reason);

public static class CapabilityPluginAdmissionPolicy
{
    public static CapabilityAdmissionDecision Evaluate(
        CapabilityPluginContract contract,
        CapabilityExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            contract.Validate();
            context.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new(false, exception.Message);
        }

        if (contract.Approval != CapabilityApprovalState.Approved)
        {
            return new(false, "Plugin is not approved.");
        }

        if (!contract.Techniques.Contains(context.Technique))
        {
            return new(false, "Requested analysis technique is not declared by the plugin.");
        }

        if (context.AuthorizationScope == TargetAuthorizationScope.ThirdPartyCleanRoom
            && context.Technique == AnalysisTechnique.RuntimeInstrumentation)
        {
            return new(false, "Runtime instrumentation is reserved for owned or explicitly authorized systems.");
        }

        if (context.OutboundNetworkRequested && !contract.AllowsOutboundNetwork)
        {
            return new(false, "Plugin contract does not permit outbound network access.");
        }

        if (context.SecretMaterialRequested && !contract.AllowsSecretMaterial)
        {
            return new(false, "Plugin contract does not permit secret material.");
        }

        if ((int)context.RequestedDataExposure > (int)contract.MaximumDataExposure)
        {
            return new(false, "Requested data exposure exceeds the plugin contract.");
        }

        return new(true, "Approved by capability plugin admission policy.");
    }
}
