using System.Reflection;
using System.Text.Json;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Connections.Surface;

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
    public const string CallDescription = "Call a neuron method. arguments is a JSON array, one element per method argument, omitting CancellationToken. Watch and Unwatch are not callable. To use an app: IApp.Request with [operation, input]; then read the neuron the app's spec names for the result.";
    public const string ContextDescription = "Read who you are acting as, the current brain id and scope for forming neuron keys, and the brains you are a member of.";
}

// The caller is stamped by the host before any of these run; each neuron's own access decides. What
// exists is the kernel's answer for the caller's brain; this process only needs a contract to call it.
// A migrated plain contract (06-03) is called through the host's IPlainNeurons; it has no grain reference to watch.
public sealed class NeuronSurface(IIntoChatOS brain, IKernelView kernel, NeuronTypeSearch search, IReadOnlySet<string>? allowed = null,
    IntoChat.OS.Runtime.IPlainNeurons? plain = null)
{
    private static readonly HashSet<string> ObserverMethods = [nameof(INeuron.Watch), nameof(INeuron.Unwatch)];
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly MethodInfo GetNeuron = typeof(IIntoChatOS).GetMethod(nameof(IIntoChatOS.Get))!;

    // The contracts one app composes: nothing outside them is listed, found or callable.
    public async Task<NeuronSurface> Restrict(IReadOnlyCollection<string> names, CancellationToken ct)
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
        var type = await Contract(contract, ct).ConfigureAwait(false);
        if (IntoChat.OS.Runtime.PlainNeuronContracts.Is(type)) { return Plain().IdOf(type, key).ToString(); }
        return ((IAddressable)Neuron(type, key)).GetGrainId().ToString();
    }

    public async Task<JsonElement?> Call(string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        JsonElement[] payload;
        try { payload = JsonSerializer.Deserialize<JsonElement[]>(arguments, Json) ?? []; }
        catch (JsonException error) { throw new ArgumentException("arguments must be a JSON array: " + error.Message, nameof(arguments)); }
        var target = type.GetMethods().Concat(type.GetInterfaces().SelectMany(inherited => inherited.GetMethods()))
            .Where(candidate => candidate.Name == method && !ObserverMethods.Contains(candidate.Name))
            .SingleOrDefault(candidate => candidate.GetParameters().Count(IsArgument) == payload.Length)
            ?? throw new ArgumentException($"{type.Name} has no method {method} taking {payload.Length} arguments.");
        var parameters = target.GetParameters();
        var values = new object?[parameters.Length];
        for (int index = 0, argument = 0; index < parameters.Length; index++)
        {
            try { values[index] = IsArgument(parameters[index]) ? payload[argument++].Deserialize(parameters[index].ParameterType, Json) : ct; }
            catch (JsonException error) { throw new ArgumentException($"Argument {argument - 1} of {method}: {error.Message}", nameof(arguments)); }
        }
        object? returned;
        try { returned = target.Invoke(Neuron(type, key), values); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { throw error.InnerException; }
        if (returned is not Task task) { throw new NotSupportedException($"{type.Name}.{target.Name} does not return a Task."); }
        var result = await TaskBoxing.ResultAsync(task, target.ReturnType).ConfigureAwait(false);
        if (!target.ReturnType.IsGenericType) { return null; }
        return JsonSerializer.SerializeToElement(result, target.ReturnType.GetGenericArguments()[0], Json);
    }

    // The neuron a contract and key name, as a reference: what a watch subscribes to.
    public async Task<INeuron> Reference(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        if (IntoChat.OS.Runtime.PlainNeuronContracts.Is(type))
        { throw new NotSupportedException($"{type.Name} is a plain neuron: watch it through PlainId and the session synapse neuron."); }
        return (INeuron)Neuron(type, key);
    }

    // The runtime id of the neuron a plain contract and key name (06-07b: a plain neuron is watched through the session synapse
    // neuron, not a reference), or null when the contract is still a grain contract.
    public async Task<global::DigitalBrain.NeuronId?> PlainId(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var type = await Contract(contract, ct).ConfigureAwait(false);
        return IntoChat.OS.Runtime.PlainNeuronContracts.Is(type) ? Plain().IdOf(type, key) : null;
    }

    private bool Allowed(string name) => allowed is null || allowed.Contains(name);

    private async Task<Dictionary<string, TypeEntry>> Neurons(CancellationToken ct)
        => (await kernel.Types(ct).ConfigureAwait(false)).Where(entry => entry.Kind == TypeKind.Neuron)
            .ToDictionary(entry => entry.Name, StringComparer.Ordinal);

    private TypeEntry Entry(IReadOnlyDictionary<string, TypeEntry> neurons, string name)
        => !Allowed(name) ? throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list.")
            : neurons.TryGetValue(name, out var entry) ? entry
            : throw new ArgumentException($"{name} is not a neuron type of this brain. Pass the full type name from neurons_list.");

    private async Task<Type> Contract(string name, CancellationToken ct)
        => LoadedContracts.Find(Entry(await Neurons(ct).ConfigureAwait(false), name).Name);

    // A contract several neurons implement (IToolSource) has no grain type of its own; the model gets
    // the brain's reason, not a reflection wrapper, so it can switch to the hit's own contract.
    private object Neuron(Type contract, string key)
    {
        if (IntoChat.OS.Runtime.PlainNeuronContracts.Is(contract))
        {
            try { return Plain().Get(contract, key); }
            catch (ArgumentException error) { throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.Message}", error); }
        }
        try { return GetNeuron.MakeGenericMethod(contract).Invoke(brain, [new NeuronId(key)])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.InnerException.Message}", error.InnerException); }
    }

    private IntoChat.OS.Runtime.IPlainNeurons Plain()
        => plain ?? throw new NotSupportedException("This host cannot reach plain neurons: it registers no IPlainNeurons (the silo's kernel composition does).");

    private static bool IsArgument(ParameterInfo parameter) => parameter.ParameterType != typeof(CancellationToken);
}

// One existing neuron as the surface lists it; Contract is the grain type when no deployed contract names it.
public sealed record NeuronInstance(string Contract, string Key, DateTimeOffset Created, string? Creator);
