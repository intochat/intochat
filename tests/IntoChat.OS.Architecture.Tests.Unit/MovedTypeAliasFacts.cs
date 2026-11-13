using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orleans;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Types that move from the OS into DigitalBrain keep deserializing only if everything persisted carries its old name
// as [Alias] before it moves. MovedTypes.json records, per moved type, whether it reaches storage or the signal
// journal and its current alias; rewrite it from MovedTypes.Capture() when the set changes.
public sealed class MovedTypeAliasFacts
{
    [Fact]
    public void EveryPersistedMovedTypeHasAnAlias()
    {
        foreach (var entry in MovedTypes.Load().Where(e => e.Persisted))
        {
            Assert.False(string.IsNullOrEmpty(entry.Alias), $"{entry.Old} reaches storage and must carry [Alias] before it moves");
        }
    }

    [Fact]
    public void TheInventoryMatchesTheCode()
    {
        var recorded = MovedTypes.Load().OrderBy(e => e.Old, StringComparer.Ordinal).ToArray();
        Assert.Equal(MovedTypes.Capture().OrderBy(e => e.Old, StringComparer.Ordinal), recorded);
    }
}

internal sealed record Inventory(string Method, MovedTypes[] Types);

internal sealed record MovedTypes(string Old, string? Alias, bool Persisted)
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Path => System.IO.Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "MovedTypes.json");

    // Top-level types still in the OS awaiting their move (spec section 4.1), by CLR full name.
    private static readonly string[] Names =
    [
        "IntoChat.OS.Framework.NeutralCallMapping", "IntoChat.OS.Framework.NeutralCallFilter", "IntoChat.OS.Framework.TaskBoxing",
        "IntoChat.OS.Framework.IRevisioned`1",
        "IntoChat.OS.Framework.Modules.OsModule",
        "IntoChat.OS.NeuronAliases", "IntoChat.OS.Runtime.NeuronProxies",
    ];

    // Public types moved from IntoChat.OS.* into DigitalBrain.* packages in 0.13.
    private static readonly (string Old, Type New, string? ExplicitAlias, bool ExplicitPersisted)[] Moved =
    [
        ("IntoChat.OS.Activated", typeof(DigitalBrain.Signals.Activated), null, false),
        ("IntoChat.OS.Connections.Configuration.HostConfiguration", typeof(DigitalBrain.Capabilities.Connections.Connectors.HostConfiguration), null, false),
        ("IntoChat.OS.Connections.Contracts.ICapabilities", typeof(DigitalBrain.Capabilities.Connections.Integrations.ICapabilities), null, false),
        ("IntoChat.OS.Connections.Contracts.IRuntimeCapabilitySource", typeof(DigitalBrain.Capabilities.Connections.Integrations.IRuntimeCapabilitySource), null, false),
        ("IntoChat.OS.Connections.Contracts.NeuronType", typeof(DigitalBrain.Capabilities.Connections.Integrations.NeuronType), "registry.neuron-type", true),
        ("IntoChat.OS.Connections.Contracts.NeuronTypeHit", typeof(DigitalBrain.Capabilities.Connections.Integrations.NeuronTypeHit), "registry.type-hit", true),
        ("IntoChat.OS.Connections.Platform.Connections.BrainSeal", typeof(DigitalBrain.Capabilities.Connections.Sealing.BrainSeal), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionKeys", typeof(DigitalBrain.Capabilities.Connections.ConnectionKeys), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionNeuron`1", typeof(DigitalBrain.Capabilities.Connections.ConnectionNeuron<>), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionNeuronTypes", typeof(DigitalBrain.Capabilities.Connections.ConnectionNeuronTypes), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionProbeOutcome", typeof(DigitalBrain.Capabilities.Connections.ConnectionProbeOutcome), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionProbeResult", typeof(DigitalBrain.Capabilities.Connections.ConnectionProbeResult), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionState", typeof(DigitalBrain.Capabilities.Connections.ConnectionState), "connection.state", true),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionStatus", typeof(DigitalBrain.Capabilities.Connections.ConnectionStatus), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionView", typeof(DigitalBrain.Capabilities.Connections.ConnectionView), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Credential", typeof(DigitalBrain.Capabilities.Connections.Credential), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.IBrainSecretSealer", typeof(DigitalBrain.Capabilities.Connections.Sealing.IBrainSecretSealer), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.IConnection", typeof(DigitalBrain.Capabilities.Connections.IConnection), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.IConnectionInbound", typeof(DigitalBrain.Capabilities.Connections.IConnectionInbound), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.IOAuthTokenExchange", typeof(DigitalBrain.Capabilities.Connections.Connectors.IOAuthTokenExchange), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.ISecretSealer", typeof(DigitalBrain.Capabilities.Connections.Sealing.ISecretSealer), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthExternalOperations", typeof(DigitalBrain.Capabilities.Connections.Connectors.OAuthExternalOperations), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthGrant", typeof(DigitalBrain.Capabilities.Connections.OAuthGrant), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthMethod", typeof(DigitalBrain.Capabilities.Connections.Connectors.OAuthMethod), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthTokenExchangeException", typeof(DigitalBrain.Capabilities.Connections.Connectors.OAuthTokenExchangeException), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthTokenFailure", typeof(DigitalBrain.Capabilities.Connections.Connectors.OAuthTokenFailure), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.SealedSecret", typeof(DigitalBrain.Capabilities.Secrets.SealedSecret), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionConnected", typeof(DigitalBrain.Capabilities.Connections.ConnectionConnected), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionDisconnected", typeof(DigitalBrain.Capabilities.Connections.ConnectionDisconnected), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionExpired", typeof(DigitalBrain.Capabilities.Connections.ConnectionExpired), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionFailing", typeof(DigitalBrain.Capabilities.Connections.ConnectionFailing), null, false),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionUpdated", typeof(DigitalBrain.Capabilities.Connections.ConnectionUpdated), null, false),
        ("IntoChat.OS.Connections.Platform.Identity.IBrainOwnerDispatch", typeof(DigitalBrain.Capabilities.Connections.IBrainOwnerDispatch), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.Capability", typeof(DigitalBrain.Capabilities.Connections.Integrations.IntegrationCapability), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.ConfigureIntegration", typeof(DigitalBrain.Capabilities.Connections.Integrations.ConfigureIntegration), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.ConnectorDefinition", typeof(DigitalBrain.Capabilities.Connections.Connectors.ConnectorDefinition), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.ICapabilitySource", typeof(DigitalBrain.Capabilities.Connections.Integrations.ICapabilitySource), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.IIntegrationHandle", typeof(DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.IIntegrationStartup", typeof(DigitalBrain.Capabilities.Connections.Integrations.IIntegrationStartup), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.IModuleIntegrations`1", typeof(DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<>), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.IntegrationSnapshot", typeof(DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.IntegrationStatus", typeof(DigitalBrain.Capabilities.Connections.Integrations.IntegrationStatus), null, false),
        ("IntoChat.OS.Connections.Platform.Integrations.ReleasedIntegration", typeof(DigitalBrain.Capabilities.Connections.Integrations.ReleasedIntegration), null, false),
        ("IntoChat.OS.Contracts.Compute.BudgetExhaustedException", typeof(DigitalBrain.Capabilities.Compute.BudgetExhaustedException), null, false),
        ("IntoChat.OS.Contracts.Compute.ComputeTracePage", typeof(DigitalBrain.Capabilities.Compute.ComputeTracePage), null, false),
        ("IntoChat.OS.Contracts.Compute.Cost", typeof(DigitalBrain.Capabilities.Compute.Cost), null, false),
        ("IntoChat.OS.Contracts.Compute.CostKind", typeof(DigitalBrain.Capabilities.Compute.CostKind), null, false),
        ("IntoChat.OS.Contracts.Compute.CostRow", typeof(DigitalBrain.Capabilities.Compute.CostRow), null, false),
        ("IntoChat.OS.Contracts.Compute.CostSubject", typeof(DigitalBrain.Capabilities.Compute.CostSubject), null, false),
        ("IntoChat.OS.Contracts.Compute.CostSummary", typeof(DigitalBrain.Capabilities.Compute.CostSummary), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalAccountingStatus", typeof(DigitalBrain.Capabilities.Compute.ExternalAccountingStatus), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallCompletion", typeof(DigitalBrain.Capabilities.Compute.ExternalCallCompletion), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallHandle", typeof(DigitalBrain.Capabilities.Compute.ExternalCallHandle), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallOutcome", typeof(DigitalBrain.Capabilities.Compute.ExternalCallOutcome), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallOutcomeKind", typeof(DigitalBrain.Capabilities.Compute.ExternalCallOutcomeKind), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallRequest", typeof(DigitalBrain.Capabilities.Compute.ExternalCallRequest), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalCallState", typeof(DigitalBrain.Capabilities.Compute.ExternalCallState), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalChargePolicy", typeof(DigitalBrain.Capabilities.Compute.ExternalChargePolicy), null, false),
        ("IntoChat.OS.Contracts.Compute.ExternalOperationDescriptor", typeof(DigitalBrain.Capabilities.Compute.ExternalOperationDescriptor), null, false),
        ("IntoChat.OS.Contracts.Compute.Funding", typeof(DigitalBrain.Capabilities.Compute.Funding), null, false),
        ("IntoChat.OS.Contracts.Compute.FundingKind", typeof(DigitalBrain.Capabilities.Compute.FundingKind), null, false),
        ("IntoChat.OS.Contracts.Compute.ICostMeter", typeof(DigitalBrain.Capabilities.Compute.ICostMeter), null, false),
        ("IntoChat.OS.Contracts.Compute.IPriceList", typeof(DigitalBrain.Capabilities.Compute.IPriceList), null, false),
        ("IntoChat.OS.Contracts.Compute.PriceQuote", typeof(DigitalBrain.Capabilities.Compute.PriceQuote), null, false),
        ("IntoChat.OS.Contracts.Compute.PricingStatus", typeof(DigitalBrain.Capabilities.Compute.PricingStatus), null, false),
        ("IntoChat.OS.Contracts.Compute.TraceDetailStatus", typeof(DigitalBrain.Capabilities.Compute.TraceDetailStatus), null, false),
        ("IntoChat.OS.Contracts.Compute.UsageGrouping", typeof(DigitalBrain.Capabilities.Compute.UsageGrouping), null, false),
        ("IntoChat.OS.Contracts.Configuration.ConfigurationKeyGrammar", typeof(DigitalBrain.Modules.Configuration.ConfigurationKeyGrammar), null, false),
        ("IntoChat.OS.Created", typeof(DigitalBrain.Signals.Created), null, false),
        ("IntoChat.OS.Framework.Actor", typeof(DigitalBrain.Access.Actor), null, false),
        ("IntoChat.OS.Framework.BrainKey", typeof(DigitalBrain.Brains.BrainKey), null, false),
        ("IntoChat.OS.Framework.BrainScope", typeof(DigitalBrain.Brains.BrainScope), null, false),
        ("IntoChat.OS.Framework.Caller", typeof(DigitalBrain.Access.Caller), null, false),
        ("IntoChat.OS.Framework.CallerContextStamper", typeof(DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext), null, false),
        ("IntoChat.OS.Framework.Compute.TraceContext", typeof(DigitalBrain.Capabilities.Compute.TraceContext), null, false),
        ("IntoChat.OS.Framework.Compute.TraceOutgoingFilter", typeof(DigitalBrain.Runtime.Orleans.Compute.TraceRequestContext), null, false),
        ("IntoChat.OS.Framework.Configuration.ModuleSettingsValidation", typeof(DigitalBrain.Modules.Options.ModuleSettingsValidation), null, false),
        ("IntoChat.OS.Framework.Diagnostics.Swallow", typeof(DigitalBrain.Log.Swallow), null, false),
        ("IntoChat.OS.Framework.Modules.CallerAdmission", typeof(DigitalBrain.Brains.CallerAdmission), null, false),
        ("IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration", typeof(DigitalBrain.Modules.Configuration.ModuleConfiguration), null, false),
        ("IntoChat.OS.Framework.Modules.Configuration.ModuleOptionsSerialization", typeof(DigitalBrain.Modules.Options.ModuleOptionsSerialization), null, false),
        ("IntoChat.OS.Framework.Modules.Http.ForwardedHeadersHosting", typeof(DigitalBrain.Runtime.Http.Surface.ForwardedHeadersHosting), null, false),
        ("IntoChat.OS.Framework.Modules.Http.ForwardedHeadersSetup", typeof(DigitalBrain.Runtime.Http.Surface.ForwardedHeadersSetup), null, false),
        ("IntoChat.OS.Framework.Modules.Http.HttpProblems", typeof(DigitalBrain.Runtime.Http.Surface.HttpProblems), null, false),
        ("IntoChat.OS.Framework.Modules.Http.IRequestThrottle", typeof(DigitalBrain.Runtime.Http.Surface.IRequestThrottle), null, false),
        ("IntoChat.OS.Framework.Modules.Http.IpPrefix", typeof(DigitalBrain.Runtime.Http.Surface.IpPrefix), null, false),
        ("IntoChat.OS.Framework.Modules.Http.IpThrottleFilter", typeof(DigitalBrain.Runtime.Http.Surface.IpThrottleFilter), null, false),
        ("IntoChat.OS.Framework.Modules.Http.ReturnUrlValidator", typeof(DigitalBrain.Runtime.Http.Surface.ReturnUrlValidator), null, false),
        ("IntoChat.OS.Framework.Modules.Http.SlidingWindowThrottle", typeof(DigitalBrain.Runtime.Http.Surface.SlidingWindowThrottle), null, false),
        ("IntoChat.OS.Framework.Modules.Http.ThrottleResult", typeof(DigitalBrain.Runtime.Http.Surface.ThrottleResult), null, false),
        ("IntoChat.OS.Framework.Modules.IBrainAccess", typeof(DigitalBrain.Runtime.Http.Surface.IBrainAccess), null, false),
        ("IntoChat.OS.Framework.Modules.IHttpModule", typeof(DigitalBrain.Runtime.Http.Surface.IHttpModule), null, false),
        ("IntoChat.OS.Framework.Modules.IHttpSurface", typeof(DigitalBrain.Runtime.Http.Surface.IHttpSurface), null, false),
        ("IntoChat.OS.Framework.Modules.IModuleOptions", typeof(DigitalBrain.Modules.Options.IModuleOptions), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ExternalCallHandler", typeof(DigitalBrain.Capabilities.Compute.ExternalCallHandler), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ExternalCallHttp", typeof(DigitalBrain.Capabilities.Compute.ExternalCallHttp), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ExternalCallMetadata", typeof(DigitalBrain.Capabilities.Compute.ExternalCallMetadata), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.IExternalCallContextAccessor", typeof(DigitalBrain.Capabilities.Compute.IExternalCallContextAccessor), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ISingleAttemptHttpTransport", typeof(DigitalBrain.Capabilities.Compute.ISingleAttemptHttpTransport), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.MeteredHttpClientRegistration", typeof(DigitalBrain.Capabilities.Compute.MeteredHttpClientRegistration), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.MeteredHttpClients", typeof(DigitalBrain.Capabilities.Compute.MeteredHttpClients), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ModulePriceListRegistration", typeof(DigitalBrain.Capabilities.Compute.ModulePriceListRegistration), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ModulePriceListRegistry", typeof(DigitalBrain.Capabilities.Compute.ModulePriceListRegistry), null, false),
        ("IntoChat.OS.Framework.Modules.Metering.ModulePriceLists", typeof(DigitalBrain.Capabilities.Compute.ModulePriceLists), null, false),
        ("IntoChat.OS.Framework.Modules.ModuleId", typeof(DigitalBrain.Modules.Options.ModuleId), null, false),
        ("IntoChat.OS.Framework.Modules.Platform.Integrations.IntegrationSchema", typeof(DigitalBrain.Capabilities.Connections.Connectors.IntegrationSchema), null, false),
        ("IntoChat.OS.Framework.Modules.SettingAttribute", typeof(DigitalBrain.Modules.Options.SettingAttribute), null, false),
        ("IntoChat.OS.Framework.Parts.IMasterKeyCustody", typeof(DigitalBrain.Identity.Local.Custody.IMasterKeyCustody), null, false),
        ("IntoChat.OS.Framework.Parts.MasterKeyCustodyKind", typeof(DigitalBrain.Identity.Local.Custody.MasterKeyCustodyKind), null, false),
        ("IntoChat.OS.Framework.UntrustedCallerException", typeof(DigitalBrain.Runtime.Orleans.Capabilities.UntrustedCallerException), null, false),
        ("IntoChat.OS.IBrain", typeof(DigitalBrain.Brains.IBrain), null, false),
        ("IntoChat.OS.InstanceCreated", typeof(DigitalBrain.Signals.InstanceCreated), null, false),
        ("IntoChat.OS.InstanceRetired", typeof(DigitalBrain.Signals.InstanceRetired), null, false),
        ("IntoChat.OS.Integrations.ConnectionRef", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionRef), null, false),
        ("IntoChat.OS.Integrations.ConnectionRequested", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionRequested), null, false),
        ("IntoChat.OS.Integrations.ConnectionSummary", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionSummary), null, false),
        ("IntoChat.OS.Integrations.ConnectionsChanged", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionsChanged), null, false),
        ("IntoChat.OS.Integrations.ConnectorKind", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectorKind), null, false),
        ("IntoChat.OS.Integrations.ConnectorOffer", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectorOffer), null, false),
        ("IntoChat.OS.Integrations.IConnectionRequests", typeof(DigitalBrain.Capabilities.Connections.Requests.IConnectionRequests), null, false),
        ("IntoChat.OS.Owner", typeof(DigitalBrain.Brains.Owner), null, false),
        ("IntoChat.OS.Runtime.ConnectionContractAttribute", typeof(DigitalBrain.Contracts.ConnectionContractAttribute), null, false),
        ("IntoChat.OS.Runtime.PlainNeuronContracts", typeof(DigitalBrain.Contracts.PlainNeuronContracts), null, false),
        ("IntoChat.OS.ServiceUnavailableException", typeof(DigitalBrain.Errors.ServiceUnavailableException), "framework.service-unavailable", true),
        ("IntoChat.OS.StaleActionException", typeof(DigitalBrain.Errors.StaleActionException), "ui.stale-action", true),
        ("IntoChat.OS.State", typeof(DigitalBrain.Brains.BrainState), null, false),
        ("IntoChat.OS.Trace", typeof(DigitalBrain.Capabilities.Compute.Trace), null, false),
        ("IntoChat.OS.TypeAdded", typeof(DigitalBrain.Signals.TypeAdded), null, false),
    ];

    internal static MovedTypes[] Load() => JsonSerializer.Deserialize<Inventory>(File.ReadAllText(Path), Format)!.Types;

    // Persisted: [GenerateSerializer] (itself or a nested type), a field, property or array element type of a
    // [GenerateSerializer] type in the production assemblies, a DigitalBrain.Signal, or a moved DigitalBrain type
    // with an Orleans surrogate or persisted state/wire alias.
    internal static MovedTypes[] Capture()
    {
        var all = ProductionArchitecture.Types;
        var referenced = all.Where(t => t.IsDefined(typeof(GenerateSerializerAttribute), inherit: false))
            .SelectMany(t => t.GetFields(Members).Select(f => f.FieldType).Concat(t.GetProperties(Members).Select(p => p.PropertyType)))
            .SelectMany(Unwrap).ToHashSet();
        var surrogates = ContractManifest.SurrogateMap();

        var inTree = Names.Select(name =>
        {
            var type = all.Single(t => t.FullName == name);
            var persisted = type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false)
                || type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Any(n => n.IsDefined(typeof(GenerateSerializerAttribute), inherit: false))
                || referenced.Contains(type)
                || typeof(DigitalBrain.Signal).IsAssignableFrom(type);
            return new MovedTypes(name, type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias, persisted);
        });

        var moved = Moved.Select(entry =>
        {
            surrogates.TryGetValue(entry.New, out var surrogate);
            var alias = entry.New.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias
                ?? surrogate?.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias
                ?? entry.New.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias
                ?? entry.New.GetCustomAttribute<DigitalBrain.Contracts.NeuronAliasAttribute>(inherit: false)?.Alias
                ?? entry.ExplicitAlias;
            var persisted = entry.New.IsDefined(typeof(GenerateSerializerAttribute), inherit: false)
                || entry.New.IsDefined(typeof(DigitalBrain.State.ExportableStateAttribute), inherit: false)
                || surrogate is not null
                || typeof(DigitalBrain.Signal).IsAssignableFrom(entry.New)
                || entry.ExplicitPersisted;
            return new MovedTypes(entry.Old, alias, persisted);
        });

        return [.. inTree.Concat(moved).OrderBy(e => e.Old, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (type.HasElementType) { foreach (var inner in Unwrap(type.GetElementType()!)) { yield return inner; } }
        if (type.IsGenericType) { foreach (var inner in type.GetGenericArguments().SelectMany(Unwrap)) { yield return inner; } }
    }
}
