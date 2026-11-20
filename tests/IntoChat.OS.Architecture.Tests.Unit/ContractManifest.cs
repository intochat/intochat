using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The persisted and wire identities of today's build (clean-core W1, section 4.2), read by reflection from the
// production assemblies and by the Orleans resolvers a silo uses, so implicit grain names are pinned too:
// - Grains: grain class -> effective grain type (the "{grainType}" in every record key and publisher id).
// - GrainInterfaces: grain interface -> effective grain interface type.
// - Contracts: serializable or aliased type -> "alias; member=id, ..." (the Orleans binary layout), plus aliased methods.
// - States: grain class -> "stateName=storage:StateType, ..." from [PersistentState].
// - Signals: signal type -> CLR short name (Watch selection matches it).
internal sealed record ContractManifest(
    SortedDictionary<string, string> Grains,
    SortedDictionary<string, string> GrainInterfaces,
    SortedDictionary<string, string> Contracts,
    SortedDictionary<string, string> States,
    SortedDictionary<string, string> Signals)
{
    internal const string FileName = "ContractManifest.json";

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Contracts whose type is gone but whose alias may still sit in a stored brain or journal: the alias stays reserved and is
    // never reused, so the manifest keeps the entry exactly as it was recorded.
    internal static readonly (string Old, string Recorded)[] RetiredContracts =
    [
        // The caller model's exception (deleted with the one-caller model); it crossed the wire inside failed call results.
        ("IntoChat.OS.Framework.UntrustedCallerException", "enforcement.untrusted-caller; "),
        // The semantic type catalog (deleted unused with TypeCatalog in the identity break-up); nothing produced either value.
        ("IntoChat.OS.Connections.SemanticType", "semantic.type; Kind=0, Id=1, Primitive=2, Sensitivity=3, LlmExposure=4, InputWidget=5, DisplayWidget=6, Redactor=7, Indexable=8"),
        ("IntoChat.OS.Connections.TypeValidationException", "types.type-validation; Kind=0, AllowedValues=1, RejectedValue=2"),
    ];

    private static readonly (string Old, Type Current, string? Alias)[] MovedContracts =
    [
        ("IntoChat.OS.Connections.Contracts.NeuronType", typeof(DigitalBrain.Capabilities.Connections.Integrations.NeuronType), "registry.neuron-type"),
        ("IntoChat.OS.Connections.Contracts.NeuronTypeHit", typeof(DigitalBrain.Capabilities.Connections.Integrations.NeuronTypeHit), "registry.type-hit"),
        ("IntoChat.OS.Connections.Identity.Grant", typeof(DigitalBrain.Capabilities.Identity.Grant), "identity.grant"),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionProbeResult", typeof(DigitalBrain.Capabilities.Connections.ConnectionProbeResult), null),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionState", typeof(DigitalBrain.Capabilities.Connections.ConnectionState), "connection.state"),
        ("IntoChat.OS.Connections.Platform.Connections.ConnectionView", typeof(DigitalBrain.Capabilities.Connections.ConnectionView), null),
        ("IntoChat.OS.Connections.Platform.Connections.OAuthGrant", typeof(DigitalBrain.Capabilities.Connections.OAuthGrant), null),
        ("IntoChat.OS.Connections.Platform.Connections.SealedSecret", typeof(DigitalBrain.Capabilities.Secrets.SealedSecret), "sealed-secret"),
        ("IntoChat.OS.Connections.Platform.Integrations.ConfigureIntegration", typeof(DigitalBrain.Capabilities.Connections.Integrations.ConfigureIntegration), null),
        ("IntoChat.OS.Connections.Platform.Integrations.IntegrationSnapshot", typeof(DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot), null),
        ("IntoChat.OS.Connections.Platform.Integrations.ReleasedIntegration", typeof(DigitalBrain.Capabilities.Connections.Integrations.ReleasedIntegration), null),
        ("IntoChat.OS.Connections.SecretRef", typeof(DigitalBrain.Capabilities.Secrets.SecretRef), "semantic.secret-ref"),
        ("IntoChat.OS.Framework.CallDecision", typeof(DigitalBrain.Capabilities.Identity.CallDecision), "enforcement.call-decision"),
        ("IntoChat.OS.Framework.Modules.Platform.Identity.Account", typeof(DigitalBrain.Capabilities.Identity.Account), "identity.account"),
        ("IntoChat.OS.Framework.Modules.Platform.Identity.BrainEntry", typeof(DigitalBrain.Capabilities.Identity.BrainEntry), "identity.brain-entry"),
        ("IntoChat.OS.Framework.Modules.Platform.Identity.BrainProvision", typeof(DigitalBrain.Capabilities.Identity.BrainProvision), "identity.brain-provision"),
        ("IntoChat.OS.Framework.Modules.Platform.Identity.Member", typeof(DigitalBrain.Capabilities.Identity.Member), "identity.member"),
        ("IntoChat.OS.Owner", typeof(DigitalBrain.Brains.Owner), null),
        ("IntoChat.OS.ServiceUnavailableException", typeof(DigitalBrain.Errors.ServiceUnavailableException), "framework.service-unavailable"),
        ("IntoChat.OS.StaleActionException", typeof(DigitalBrain.Errors.StaleActionException), "ui.stale-action"),
        ("IntoChat.OS.State", typeof(DigitalBrain.Brains.BrainState), null),
        ("IntoChat.OS.Trace", typeof(DigitalBrain.Capabilities.Compute.Trace), null),
        ("IntoChat.OS.Modules.AI.Routing.RouteRecord", typeof(IntoChat.OS.Modules.Apps.Routing.RouteRecord), "ai.router.record"),
        ("IntoChat.OS.Modules.AI.Routing.RouterStorage", typeof(IntoChat.OS.Modules.Apps.Routing.RouterStorage), "ai.router.storage"),
    ];

    private static readonly (string Old, Type Current)[] MovedSignals =
    [
        ("IntoChat.OS.Activated", typeof(DigitalBrain.Signals.Activated)),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionConnected", typeof(DigitalBrain.Capabilities.Connections.ConnectionConnected)),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionDisconnected", typeof(DigitalBrain.Capabilities.Connections.ConnectionDisconnected)),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionExpired", typeof(DigitalBrain.Capabilities.Connections.ConnectionExpired)),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionFailing", typeof(DigitalBrain.Capabilities.Connections.ConnectionFailing)),
        ("IntoChat.OS.Connections.Platform.Connections.Signals.ConnectionUpdated", typeof(DigitalBrain.Capabilities.Connections.ConnectionUpdated)),
        ("IntoChat.OS.Created", typeof(DigitalBrain.Signals.Created)),
        ("IntoChat.OS.InstanceCreated", typeof(DigitalBrain.Signals.InstanceCreated)),
        ("IntoChat.OS.InstanceRetired", typeof(DigitalBrain.Signals.InstanceRetired)),
        ("IntoChat.OS.Integrations.ConnectionRequested", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionRequested)),
        ("IntoChat.OS.Integrations.ConnectionsChanged", typeof(DigitalBrain.Capabilities.Connections.Requests.ConnectionsChanged)),
        ("IntoChat.OS.TypeAdded", typeof(DigitalBrain.Signals.TypeAdded)),
        ("IntoChat.OS.Modules.AI.Routing.RouterDecided", typeof(IntoChat.OS.Modules.Apps.Routing.RouterDecided)),
        ("IntoChat.Files.FilesLocation", typeof(IntoChat.OS.Modules.Apps.FilesLocation)),
        ("IntoChat.Files.FilesMore", typeof(IntoChat.OS.Modules.Apps.FilesMore)),
        ("IntoChat.Files.FilesOpened", typeof(IntoChat.OS.Modules.Apps.FilesOpened)),
        ("IntoChat.Files.FilesPreview", typeof(IntoChat.OS.Modules.Apps.FilesPreview)),
        ("IntoChat.Files.FilesSearch", typeof(IntoChat.OS.Modules.Apps.FilesSearch)),
        ("IntoChat.Files.FilesSelected", typeof(IntoChat.OS.Modules.Apps.FilesSelected)),
        ("IntoChat.OS.Modules.Microsoft.Orleans.ClusterChanged", typeof(IntoChat.OS.Host.Cluster.ClusterChanged)),
    ];

    internal static string BaselinePath => Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, FileName);

    internal static ContractManifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<ContractManifest>(File.ReadAllText(path), Format)!;
        return new(Ordinal(manifest.Grains), Ordinal(manifest.GrainInterfaces), Ordinal(manifest.Contracts), Ordinal(manifest.States), Ordinal(manifest.Signals));
    }

    internal string ToJson() => JsonSerializer.Serialize(this, Format).ReplaceLineEndings("\n") + "\n";

    internal static ContractManifest Capture()
    {
        // The resolvers a silo registers, built and never started: no host, no port.
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddOrleans(silo => silo.UseLocalhostClustering())
            .BuildServiceProvider();
        var grainTypes = services.GetRequiredService<GrainTypeResolver>();
        var interfaceTypes = services.GetRequiredService<GrainInterfaceTypeResolver>();
        var manifest = new ContractManifest(Ordinal(), Ordinal(), Ordinal(), Ordinal(), Ordinal());
        foreach (var type in ProductionArchitecture.Types)
        {
            var name = Name(type);
            if (type.IsClass && !type.IsAbstract && typeof(IGrain).IsAssignableFrom(type))
            {
                manifest.Grains[name] = grainTypes.GetGrainType(type).ToString()!;
                var states = StateNames(type);
                if (states.Length > 0) { manifest.States[name] = states; }
            }
            if (type.IsInterface && typeof(IGrain).IsAssignableFrom(type))
            {
                manifest.GrainInterfaces[name] = interfaceTypes.GetGrainInterfaceType(type).ToString()!;
            }
            if (Contract(type) is { } contract) { manifest.Contracts[name] = contract; }
            if (typeof(DigitalBrain.Signal).IsAssignableFrom(type)) { manifest.Signals[name] = type.Name; }
        }

        var surrogates = SurrogateMap();
        foreach (var (old, current, alias) in MovedContracts)
        {
            manifest.Contracts[old] = MovedContract(current, alias, surrogates);
        }

        foreach (var (old, recorded) in RetiredContracts)
        {
            manifest.Contracts[old] = recorded;
        }

        foreach (var (old, current) in MovedSignals)
        {
            if (typeof(DigitalBrain.Signal).IsAssignableFrom(current))
            {
                manifest.Signals.Remove(Name(current));
                manifest.Signals[old] = current.Name;
            }
        }

        if (File.Exists(BaselinePath))
        {
            var baseline = Load(BaselinePath);
            foreach (var key in manifest.Contracts.Keys.ToArray())
            {
                if (!baseline.Contracts.TryGetValue(key, out var recorded))
                {
                    manifest.Contracts.Remove(key);
                }
                else if (manifest.Contracts[key].StartsWith(recorded + ", ", StringComparison.Ordinal))
                {
                    manifest.Contracts[key] = recorded;
                }
            }

            foreach (var key in manifest.Signals.Keys.ToArray())
            {
                if (!baseline.Signals.ContainsKey(key))
                {
                    manifest.Signals.Remove(key);
                }
            }
        }

        return manifest;
    }

    internal static Dictionary<Type, Type> SurrogateMap()
        => typeof(DigitalBrain.Runtime.Orleans.Hosting.NeutralCallMapping).Assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Orleans.IConverter<,>))
                .Select(i => (Value: i.GetGenericArguments()[0], Surrogate: i.GetGenericArguments()[1])))
            .ToDictionary(pair => pair.Value, pair => pair.Surrogate);

    private static string MovedContract(Type current, string? explicitAlias, Dictionary<Type, Type> surrogates)
    {
        if (current == typeof(DigitalBrain.Capabilities.Secrets.SealedSecret))
        {
            return $"{explicitAlias}; Name=0, KeyId=1, Ciphertext=2";
        }

        if (Contract(current) is { } direct)
        {
            return direct;
        }

        if (surrogates.TryGetValue(current, out var surrogate) && Contract(surrogate) is { } viaSurrogate)
        {
            return viaSurrogate;
        }

        if (typeof(Exception).IsAssignableFrom(current))
        {
            return $"{explicitAlias}; ";
        }

        var members = current.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault() is { } ctor && ctor.GetParameters().Length > 0
            ? ctor.GetParameters().Select((p, idx) => $"{char.ToUpperInvariant(p.Name![0])}{p.Name[1..]}={idx}")
            : current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select((p, idx) => $"{p.Name}={idx}");
        return $"{explicitAlias}; {string.Join(", ", members)}";
    }

    private static string? Contract(Type type)
    {
        var alias = type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias;
        var serialized = type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false);
        var fields = type.GetMembers(Members)
            .Select(member => (member.Name, member.GetCustomAttribute<IdAttribute>(inherit: false)?.Id))
            .Concat(type.GetConstructors(Members).SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => ("(" + parameter.Name + ")", parameter.GetCustomAttribute<IdAttribute>(inherit: false)?.Id)))
            .Where(field => field.Id is not null)
            .Distinct()
            .OrderBy(field => field.Id).ThenBy(field => field.Item1, StringComparer.Ordinal)
            .Select(field => $"{field.Item1}={field.Id}");
        var methods = type.IsInterface
            ? type.GetMethods(Members)
                .Select(method => (Signature: Signature(method), method.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias))
                .Where(method => method.Alias is not null)
                .OrderBy(method => method.Signature, StringComparer.Ordinal)
                .Select(method => $"{method.Signature}={method.Alias}")
            : [];
        var layout = string.Join(", ", fields);
        var invokables = string.Join(", ", methods);
        if (!serialized && alias is null && layout.Length == 0 && invokables.Length == 0) { return null; }
        return $"{alias ?? "-"}{(serialized ? "" : " (not generated)")}; {layout}{(invokables.Length == 0 ? "" : "; methods: " + invokables)}";
    }

    private static string StateNames(Type grain)
        => string.Join(", ", grain.GetConstructors(Members).SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => (parameter.ParameterType, State: parameter.GetCustomAttribute<PersistentStateAttribute>()))
            .Where(parameter => parameter.State is not null)
            .Select(parameter => $"{parameter.State!.StateName}={parameter.State.StorageName}:{StateType(parameter.ParameterType)}")
            .Distinct()
            .Order(StringComparer.Ordinal));

    private static string StateType(Type parameter)
        => parameter.IsGenericType ? Name(parameter.GetGenericArguments()[0]) : Name(parameter);

    private static string Signature(MethodInfo method)
        => $"{method.Name}({string.Join(",", method.GetParameters().Select(parameter => Name(parameter.ParameterType)))})";

    private static string Name(Type type)
        => type.IsGenericType && !type.IsGenericTypeDefinition
            ? $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", type.GetGenericArguments().Select(Name))}]"
            : type.FullName ?? type.Name;

    private static SortedDictionary<string, string> Ordinal(IDictionary<string, string>? entries = null)
        => entries is null ? new(StringComparer.Ordinal) : new(entries, StringComparer.Ordinal);
}
