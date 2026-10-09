using System.Reflection;
using System.Text.Json;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Framework.Surface;

// One set of names and descriptions for the neuron tools, so the assistant and digitalbrain-mcp cannot drift.
public static class NeuronSurfaceTools
{
    public const string ListName = "neurons_list";
    public const string SearchName = "neurons_search";
    public const string GetName = "neurons_get";
    public const string CallName = "neurons_call";
    public const string ContextName = "neurons_context";

    public const string ListDescription = "List neuron contracts you can address. Pass the full type name to neurons_get and neurons_call.";
    public const string SearchDescription = "Search neuron contracts by what they do. Returns each match's contract type name, and methods.";
    public const string GetDescription = "Resolve a neuron. contract is a full type name from neurons_list; key is the grain key: an app is {brainScope}/apps/{name}, a connection is {brain}/connection/{connector}/{id}.";
    public const string CallDescription = "Call a neuron method. arguments is a JSON array, one element per method argument, omitting CancellationToken. Watch and Unwatch are not callable. To use an app: IApp.Request with [operation, input]; then read the neuron the app's spec names for the result.";
    public const string ContextDescription = "Read who you are acting as and the current brain id and scope, for forming neuron keys.";
}

// The caller is stamped by the host before any of these run; each neuron's own access decides.
public sealed class NeuronSurface(IDigitalBrain brain, CapabilityCatalog capabilities, NeuronTypeSearch search, IReadOnlySet<string>? allowed = null)
{
    private static readonly HashSet<string> ObserverMethods = [nameof(INeuron.Watch), nameof(INeuron.Unwatch)];
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly MethodInfo GetNeuron = typeof(IDigitalBrain).GetMethod(nameof(IDigitalBrain.Get))!;

    // The contracts one app composes: nothing outside them is listed, found or callable.
    public NeuronSurface Restrict(IReadOnlyCollection<string> names)
    {
        foreach (var name in names) { _ = capabilities.Contracts.Find(name); }
        return new(brain, capabilities, search, names.ToHashSet(StringComparer.Ordinal));
    }

    public IReadOnlyList<string> List() => [.. capabilities.Contracts.Neurons.Where(Allowed).Order(StringComparer.Ordinal)];

    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, CancellationToken ct)
    {
        // A stamped caller searches their own brain's runtime tools too; un-stamped means contracts only.
        var brainId = CallerContextStamper.TryGet(out var caller) ? caller.BrainId : null;
        var entries = await capabilities.Read(brainId, ct).ConfigureAwait(false);
        // Rank only what this app composes: ranking everything and filtering afterwards lets unrelated
        // contracts crowd the allowed ones out of the window.
        return await search.Search(query, allowed is null ? entries : [.. entries.Where(entry => Allowed(entry.Contract))], ct: ct).ConfigureAwait(false);
    }

    public string Get(string contract, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Neuron(Contract(contract), key).GetGrainId().ToString();
    }

    public async Task<JsonElement?> Call(string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var type = Contract(contract);
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
        await task.ConfigureAwait(false);
        if (!target.ReturnType.IsGenericType) { return null; }
        var result = target.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task);
        return JsonSerializer.SerializeToElement(result, target.ReturnType.GetGenericArguments()[0], Json);
    }

    private bool Allowed(string name) => allowed is null || allowed.Contains(name);

    private Type Contract(string name) => Allowed(name) ? capabilities.Contracts.Find(name)
        : throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list.");

    // A contract several neurons implement (IToolSource) has no grain type of its own; the model gets
    // the brain's reason, not a reflection wrapper, so it can switch to the hit's own contract.
    private IAddressable Neuron(Type contract, string key)
    {
        try { return (IAddressable)GetNeuron.MakeGenericMethod(contract).Invoke(brain, [new NeuronId(key)])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.InnerException.Message}", error.InnerException); }
    }

    private static bool IsArgument(ParameterInfo parameter) => parameter.ParameterType != typeof(CancellationToken);
}
