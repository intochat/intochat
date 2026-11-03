using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

internal sealed class InProcessAppRuntime(IServiceProvider services) : IAppRuntime
{
    public const string PrebuiltBehaviorsJsonKey = "__PrebuiltBehaviorsJson";
    public const string CompiledTypeKey = "__CompiledType";

    public bool Accepts(AppBundle bundle)
        => bundle is not null
            && (!string.IsNullOrWhiteSpace(bundle.CompiledType)
                || bundle.PrebuiltBehaviors is { Count: > 0 }
                || bundle.Files.Keys.Any(static k => k.EndsWith(".dll.base64", StringComparison.Ordinal)));

    public async Task<IReadOnlyDictionary<string, string>> Deploy(
        string appKey,
        Dictionary<string, string> sources,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(configuration);

        var brain = BrainKey.BrainOf(appKey);
        if (brain.Length == 0 || !appKey.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey));
        }

        if (configuration.TryGetValue(CompiledTypeKey, out var typeName) && !string.IsNullOrWhiteSpace(typeName))
        {
            var type = Type.GetType(typeName, throwOnError: false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(typeName, throwOnError: false))
                    .FirstOrDefault(t => t is not null)
                ?? throw new InvalidOperationException($"Compiled app type '{typeName}' could not be resolved.");
            var host = services.GetRequiredService<InProcessAppHost>();
            await host.AssignType(appKey, type, configuration, cancellationToken);
            var views = await host.Describe(appKey, cancellationToken);
            var prefix = appKey + "/";
            return views.ToDictionary(
                v => v.Id.StartsWith(prefix, StringComparison.Ordinal) ? v.Id[prefix.Length..] : v.Id,
                v => v.Hash,
                StringComparer.Ordinal);
        }

        var behaviors = ExtractPrebuiltBehaviors(sources, configuration);
        var programs = SiloNeurons.Get<IPrograms>(
            services,
            new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "digitalbrain.programs", "programs"));
        var program = await programs.AssignPrebuilt(appKey, behaviors, configuration).WaitAsync(cancellationToken);
        var behaviorPrefix = appKey + "/";
        return program.Behaviors
            .Where(b => b.Id.StartsWith(behaviorPrefix, StringComparison.Ordinal))
            .ToDictionary(b => b.Id[behaviorPrefix.Length..], b => b.Hash, StringComparer.Ordinal);
    }

    public async Task Retire(string appKey, CancellationToken cancellationToken)
    {
        var brain = BrainKey.BrainOf(appKey);
        if (brain.Length == 0 || !appKey.StartsWith(brain + "/apps/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey));
        }

        var programs = SiloNeurons.Get<IPrograms>(
            services,
            new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "digitalbrain.programs", "programs"));
        await programs.Retire(appKey).WaitAsync(cancellationToken);
    }

    internal static Dictionary<string, PrebuiltBehavior> ExtractPrebuiltBehaviors(
        Dictionary<string, string> sources,
        Dictionary<string, string> configuration)
    {
        var result = new Dictionary<string, PrebuiltBehavior>(StringComparer.Ordinal);
        if (configuration.TryGetValue(PrebuiltBehaviorsJsonKey, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            var decoded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PrebuiltWire>>(json);
            if (decoded is not null)
            {
                foreach (var (path, wire) in decoded)
                {
                    var assemblyBytes = Convert.FromBase64String(wire.AssemblyBase64);
                    var symbolBytes = string.IsNullOrEmpty(wire.SymbolsBase64) ? [] : Convert.FromBase64String(wire.SymbolsBase64);
                    var hash = string.IsNullOrWhiteSpace(wire.Hash)
                        ? Convert.ToHexStringLower(SHA256.HashData(assemblyBytes))
                        : wire.Hash;
                    result[path] = new PrebuiltBehavior(assemblyBytes, symbolBytes, hash);
                }
            }
        }

        foreach (var (key, value) in sources)
        {
            if (key.EndsWith(".dll.base64", StringComparison.Ordinal))
            {
                var path = key[..^".base64".Length];
                var assemblyBytes = Convert.FromBase64String(value);
                var hash = Convert.ToHexStringLower(SHA256.HashData(assemblyBytes));
                result[path] = new PrebuiltBehavior(assemblyBytes, [], hash);
            }
        }

        return result;
    }

    internal static string SerializePrebuiltBehaviors(IReadOnlyDictionary<string, PrebuiltBehavior> behaviors)
    {
        var map = behaviors.ToDictionary(
            p => p.Key,
            p => new PrebuiltWire(p.Value.Hash, Convert.ToBase64String(p.Value.Assembly), Convert.ToBase64String(p.Value.Symbols)),
            StringComparer.Ordinal);
        return System.Text.Json.JsonSerializer.Serialize(map);
    }

    private sealed record PrebuiltWire(string Hash, string AssemblyBase64, string SymbolsBase64);
}
