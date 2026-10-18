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
            if (typeof(Signal).IsAssignableFrom(type)) { manifest.Signals[name] = type.Name; }
        }
        return manifest;
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
