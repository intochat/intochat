using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Runtime;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Host.Surface;

// One set of names and descriptions for the neuron tools, so the assistant and intochat-os-mcp cannot drift.
public static class NeuronSurfaceTools
{
    public const string ListName = "neurons_list";
    public const string SearchName = "neurons_search";
    public const string GetName = "neurons_get";
    public const string CallName = "neurons_call";
    public const string ContextName = "neurons_context";

    public const string ListDescription = "List the neurons that exist in your brain: each one's contract (a full type name for neurons_get and neurons_call), key, creation time and creator. Pass contract to list one type only. To find contracts by what they do, use neurons_search.";
    public const string SearchDescription = "Search neuron contracts by what they do. Returns each match's contract type name, and methods.";
    public const string GetDescription = "Resolve a neuron. contract is a full type name from neurons_list; key is the grain key: an app is {brainScope}/apps/{name}, a connection is {brain}/connection/{connector}/{id}.";
    public const string CallDescription = "Call a neuron method. arguments is a JSON array, one element per method argument, omitting CancellationToken. To use an app: IApp.Request with [operation, input]; then read the neuron the app's spec names for the result.";
    public const string ContextDescription = "Read who you are acting as, the current brain id and scope for forming neuron keys, and the brains you are a member of.";
}

// The caller is stamped by the host before any of these run; each neuron's own access decides. What
// exists is the kernel's answer for the caller's brain; this process only needs a contract to call it.
// A migrated plain contract (06-03) is called through the host's IPlainNeurons; it has no grain reference to watch.
public sealed class OsNeuronSurface(INeuronProxies brain, IBrainObserver kernel, NeuronTypeSearch search, IReadOnlySet<string>? allowed = null,
    IServiceProvider? plain = null)
{
    private static readonly string[] ContractAssemblyPrefixes = ["IntoChat.OS", "DigitalBrain"];
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly MethodInfo GetNeuron = typeof(INeuronProxies).GetMethod(nameof(INeuronProxies.Get))!;

    // The contracts one app composes: nothing outside them is listed, found or callable.
    public async Task<OsNeuronSurface> Restrict(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var neurons = await Neurons(ct).ConfigureAwait(false);
        foreach (var name in names)
        {
            if (!neurons.ContainsKey(name)) { throw new ArgumentException($"{name} is not a neuron type of this brain. Pass the full type name from neurons_list."); }
        }
        return new(brain, kernel, search, names.ToHashSet(StringComparer.Ordinal), plain);
    }

    public async Task<IReadOnlyList<string>> Contracts(CancellationToken ct)
        => [.. (await Neurons(ct).ConfigureAwait(false)).Keys.Where(Allowed).Order(StringComparer.Ordinal)];

    // The caller's brain's instances, named by the contract that addresses them; a restricted surface
    // lists only instances of the contracts it composes.
    public async Task<IReadOnlyList<NeuronInstance>> List(string? contract, CancellationToken ct)
    {
        var neurons = await Neurons(ct).ConfigureAwait(false);
        var byType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in neurons.Values.Where(entry => Allowed(entry.Name)).OrderBy(entry => entry.Name, StringComparer.Ordinal)) { byType.TryAdd(entry.Alias, entry.Name); }
        string? filter = null;
        if (!string.IsNullOrWhiteSpace(contract)) { filter = Entry(neurons, contract).Alias; }
        List<NeuronInstance> listed = [];
        await foreach (var instance in kernel.Instances(filter, ct).ConfigureAwait(false))
        {
            if (byType.TryGetValue(instance.Type, out var name)) { listed.Add(new(name, instance.Key, instance.Created, instance.Creator)); }
            else if (allowed is null) { listed.Add(new(instance.Type, instance.Key, instance.Created, instance.Creator)); }
        }
        return [.. listed.OrderBy(entry => entry.Contract, StringComparer.Ordinal).ThenBy(entry => entry.Key, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, CancellationToken ct)
    {
        var entries = await search.Capabilities(ct).ConfigureAwait(false);
        // Rank only what this app composes: ranking everything and filtering afterwards lets unrelated
        // contracts crowd the allowed ones out of the window.
        return await search.Search(query, allowed is null ? entries : [.. entries.Where(entry => Allowed(entry.Contract))], ct: ct).ConfigureAwait(false);
    }

    public async Task<string> Get(string contract, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = NormalizeBrainPlaceholder(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        return ResolveId(type, normalizedKey).ToString();
    }

    public async Task<JsonElement?> Call(string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        key = NormalizeBrainPlaceholder(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        JsonElement[] payload;
        try { payload = string.IsNullOrWhiteSpace(arguments) ? [] : JsonSerializer.Deserialize<JsonElement[]>(arguments, Json) ?? []; }
        catch (JsonException error) { throw new ArgumentException("arguments must be a JSON array: " + error.Message, nameof(arguments)); }
        var candidates = type.GetMethods().Concat(type.GetInterfaces().SelectMany(inherited => inherited.GetMethods()))
            .Where(candidate => candidate.Name == method)
            .ToArray();
        static int TotalArguments(MethodInfo candidate) => candidate.GetParameters().Count(IsArgument);
        static int RequiredArguments(MethodInfo candidate) => candidate.GetParameters().Count(p => IsArgument(p) && !p.HasDefaultValue);
        var exactMatches = candidates.Where(candidate => TotalArguments(candidate) == payload.Length).ToArray();
        var compatibleMatches = exactMatches.Length > 0
            ? exactMatches
            : candidates.Where(candidate => RequiredArguments(candidate) <= payload.Length && payload.Length <= TotalArguments(candidate)).ToArray();
        if (compatibleMatches.Length == 0)
        {
            var overloads = candidates.Select(FormatSignature).Distinct(StringComparer.Ordinal).ToArray();
            var expected = overloads.Length > 0 ? $" Expected: {string.Join(" or ", overloads)}." : string.Empty;
            throw new ArgumentException($"{type.Name} has no method {method} taking {payload.Length} arguments.{expected}");
        }
        if (compatibleMatches.Length > 1)
        {
            throw new ArgumentException($"{type.Name}.{method} is ambiguous for {payload.Length} arguments.");
        }
        var target = compatibleMatches[0];
        var parameters = target.GetParameters();
        var values = new object?[parameters.Length];
        for (int index = 0, argument = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (!IsArgument(parameter))
            {
                values[index] = ct;
                continue;
            }
            if (argument >= payload.Length && parameter.HasDefaultValue)
            {
                values[index] = parameter.DefaultValue;
                continue;
            }
            try
            {
                var argIndex = argument++;
                values[index] = payload[argIndex].ValueKind == JsonValueKind.Null && parameter.HasDefaultValue && parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null
                    ? parameter.DefaultValue
                    : payload[argIndex].Deserialize(parameter.ParameterType, Json);
            }
            catch (JsonException error) { throw new ArgumentException($"Argument {argument - 1} of {method}: {error.Message}", nameof(arguments)); }
        }
        object? returned;
        try { returned = target.Invoke(Neuron(type, key), values); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { throw error.InnerException; }
        if (returned is not Task task) { throw new NotSupportedException($"{type.Name}.{target.Name} does not return a Task."); }
        var result = await DigitalBrain.Invocation.TaskBoxing.ReturnResultAsync(task, target.ReturnType).ConfigureAwait(false);
        if (!target.ReturnType.IsGenericType) { return null; }
        return JsonSerializer.SerializeToElement(result, target.ReturnType.GetGenericArguments()[0], Json);
    }

    // The neuron a contract and key name, as a reference: what a watch subscribes to.
    public async Task<object> Reference(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        return Neuron(type, NormalizeBrainPlaceholder(key));
    }

    // The runtime id of the neuron a plain contract and key name (06-07b: a plain neuron is watched through the session synapse
    // neuron, not a reference), or null when the contract is still a grain contract.
    public async Task<DigitalBrain.NeuronId?> PlainId(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = NormalizeBrainPlaceholder(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        return DigitalBrain.Contracts.PlainNeuronContracts.Is(type) ? ResolveId(type, normalizedKey) : null;
    }

    private string NormalizeBrainPlaceholder(string key)
    {
        if (!key.Contains("{brain}", StringComparison.OrdinalIgnoreCase) && !key.Contains("{brainScope}", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        var currentBrain = (plain?.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current?.Brain.Value;

        if (string.IsNullOrWhiteSpace(currentBrain))
        {
            return key;
        }

        return key
            .Replace("{brain}", currentBrain, StringComparison.OrdinalIgnoreCase)
            .Replace("{brainScope}", currentBrain, StringComparison.OrdinalIgnoreCase);
    }

    private bool Allowed(string name) => allowed is null || allowed.Contains(name);

    private async Task<Dictionary<string, BrainType>> Neurons(CancellationToken ct)
        => (await kernel.Types(ct).ConfigureAwait(false)).Where(entry => entry.Kind == TypeKind.Neuron)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);

    private BrainType Entry(IReadOnlyDictionary<string, BrainType> neurons, string name)
    {
        if (neurons.TryGetValue(name, out var exact))
        {
            if (!Allowed(exact.Name)) { throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list."); }
            return exact;
        }

        var match = neurons.Values.FirstOrDefault(entry =>
            string.Equals(entry.Alias, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) ||
            entry.Name.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) ||
            (entry.Name.StartsWith("I", StringComparison.Ordinal) && entry.Name[1..].Equals(name, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            if (!Allowed(match.Name)) { throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list."); }
            return match;
        }

        throw new ArgumentException($"{name} is not a neuron type of this brain. Pass the contract alias or full type name.");
    }






    private async Task<Type> Contract(string name, CancellationToken ct)
        => LoadedContracts.Find(Entry(await Neurons(ct).ConfigureAwait(false), name).Name, ContractAssemblyPrefixes);

    // A contract several neurons implement (IToolSource) has no grain type of its own; the model gets
    // the brain's reason, not a reflection wrapper, so it can switch to the hit's own contract.
    private object Neuron(Type contract, string key)
    {
        if (DigitalBrain.Contracts.PlainNeuronContracts.Is(contract))
        {
            try { return SiloNeurons.Get(Plain(), contract, ResolveId(contract, key)); }
            catch (ArgumentException error) { throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.Message}", error); }
        }
        try { return GetNeuron.MakeGenericMethod(contract).Invoke(brain, [ResolveId(contract, key)])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.InnerException.Message}", error.InnerException); }
    }

    private static DigitalBrain.NeuronId ResolveId(Type contract, string fullKey)
        => contract == typeof(IScreen) && DigitalBrain.NeuronId.TryParse(fullKey, out var screenId)
            ? screenId
            : DigitalBrain.Contracts.PlainNeuronContracts.IdOf(contract, fullKey);

    private IServiceProvider Plain()
        => plain ?? throw new NotSupportedException("This host cannot reach plain neurons: it registers no IPlainNeurons (the silo's kernel composition does).");

    private static bool IsArgument(ParameterInfo parameter) => parameter.ParameterType != typeof(CancellationToken);

    private static string FormatSignature(MethodInfo candidate)
    {
        var required = new List<string>();
        var optional = new List<string>();
        foreach (var parameter in candidate.GetParameters())
        {
            if (!IsArgument(parameter)) { continue; }
            var name = parameter.Name ?? "_";
            if (parameter.HasDefaultValue) { optional.Add(name); }
            else { required.Add(name); }
        }

        var req = string.Join(", ", required);
        var opt = optional.Count == 0
            ? string.Empty
            : required.Count == 0
                ? $"[{string.Join(", ", optional)}]"
                : $"[, {string.Join(", ", optional)}]";
        return $"{candidate.Name}({req}{opt})";
    }
}
