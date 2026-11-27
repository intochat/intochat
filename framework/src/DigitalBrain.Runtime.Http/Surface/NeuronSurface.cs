using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Description of a neuron instance.
/// </summary>
public sealed record NeuronInstance(string Contract, string Key, DateTimeOffset Created, string? Creator);

/// <summary>
/// Unified neuron discovery, contract resolution, and invocation surface.
/// Supports both proxy-based hosts (via <see cref="INeuronProxies"/> + <see cref="IBrainObserver"/>)
/// and HTTP runtime dispatch hosts (via <see cref="IRuntimeInvocationDispatcher"/>).
/// </summary>
public sealed class NeuronSurface
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly MethodInfo GetNeuron = typeof(INeuronProxies).GetMethod(nameof(INeuronProxies.Get))!;

    private readonly INeuronProxies? _proxies;
    private readonly IBrainObserver? _observer;
    private readonly INeuronContractSearch? _search;
    private readonly IContractAssemblySource? _assemblies;
    private readonly ICallerAmbient? _callers;
    private readonly IRuntimeInvocationDispatcher? _dispatcher;
    private readonly IReadOnlySet<string>? _allowed;

    /// <summary>Creates a surface backed by an <see cref="IRuntimeInvocationDispatcher"/>.</summary>
    public NeuronSurface(IRuntimeInvocationDispatcher dispatcher, IReadOnlySet<string>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _allowed = allowed;
    }

    /// <summary>Creates a unified neuron surface with optional proxy, observer, search, exact-assembly catalog, caller ambient, and dispatcher collaborators.</summary>
    [ActivatorUtilitiesConstructor]
    public NeuronSurface(
        INeuronProxies? proxies = null,
        IBrainObserver? observer = null,
        INeuronContractSearch? search = null,
        IContractAssemblySource? assemblies = null,
        ICallerAmbient? callers = null,
        IRuntimeInvocationDispatcher? dispatcher = null,
        IReadOnlySet<string>? allowed = null)
    {
        _proxies = proxies;
        _observer = observer;
        _search = search;
        _assemblies = assemblies;
        _callers = callers;
        _dispatcher = dispatcher;
        _allowed = allowed;
    }

    /// <summary>Returns a surface restricted to <paramref name="names"/> without consulting the observer.</summary>
    public NeuronSurface Restrict(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new(_proxies, _observer, _search, _assemblies, _callers, _dispatcher, names.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Validates <paramref name="names"/> against this brain's neuron types and returns a surface restricted to them.</summary>
    public async Task<NeuronSurface> Restrict(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var neurons = await Neurons(ct).ConfigureAwait(false);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            var name = CurrentName(raw);
            if (!neurons.ContainsKey(name))
            {
                throw new ArgumentException($"{raw} is not a neuron type of this brain. Pass the full type name from neurons_list.");
            }

            resolved.Add(name);
        }

        return new(_proxies, _observer, _search, _assemblies, _callers, _dispatcher, resolved);
    }

    /// <summary>Whether <paramref name="contract"/> is permitted by this surface's restriction set.</summary>
    public bool Allowed(string contract)
        => _allowed is null || _allowed.Contains(contract);

    /// <summary>Maps a contract's alias or former name to its current canonical full type name using this surface's assembly catalog.</summary>
    public string CurrentName(string name)
        => LoadedContracts.CurrentName(name, _assemblies);

    /// <summary>Finds a plain neuron contract CLR type using this surface's exact-assembly catalog.</summary>
    public Type FindContract(string name)
        => LoadedContracts.Find(name, _assemblies);

    /// <summary>Lists the canonical names of every allowed neuron type in this brain.</summary>
    public async Task<IReadOnlyList<string>> Contracts(CancellationToken ct)
        => [.. (await Neurons(ct).ConfigureAwait(false)).Keys.Where(Allowed).Order(StringComparer.Ordinal)];

    /// <summary>Lists neuron instances in the current brain, optionally filtered by contract name or alias.</summary>
    public async Task<IReadOnlyList<NeuronInstance>> List(string? contract, CancellationToken ct)
    {
        var observer = RequireObserver();
        var neurons = await Neurons(ct).ConfigureAwait(false);
        var byType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in neurons.Values.Where(entry => Allowed(entry.Name)).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            byType.TryAdd(entry.Alias, entry.Name);
        }

        string? filter = null;
        if (!string.IsNullOrWhiteSpace(contract))
        {
            filter = Entry(neurons, contract).Alias;
        }

        List<NeuronInstance> listed = [];
        await foreach (var instance in observer.Instances(filter, ct).ConfigureAwait(false))
        {
            if (byType.TryGetValue(instance.Type, out var name))
            {
                listed.Add(new(name, instance.Key, instance.Created, instance.Creator));
            }
            else if (_allowed is null)
            {
                listed.Add(new(instance.Type, instance.Key, instance.Created, instance.Creator));
            }
        }

        return [.. listed.OrderBy(entry => entry.Contract, StringComparer.Ordinal).ThenBy(entry => entry.Key, StringComparer.Ordinal)];
    }

    /// <summary>Searches available neuron contracts and live capabilities visible on this surface.</summary>
    public async Task<IReadOnlyList<NeuronTypeHit>> Search(string query, CancellationToken ct)
    {
        var search = _search ?? throw new NotSupportedException("This host registers no INeuronContractSearch.");
        var entries = await search.Capabilities(ct).ConfigureAwait(false);
        // Rank only what this surface permits so unrelated contracts cannot crowd allowed ones out.
        return await search.Search(query, _allowed is null ? entries : [.. entries.Where(entry => Allowed(entry.Contract))], ct: ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a contract and key to its canonical runtime neuron id string.</summary>
    public async Task<string> Get(string contract, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = NormalizeBrainPlaceholder(key);
        if (_observer is null)
        {
            var type = LoadedContracts.Find(contract, _assemblies);
            return ResolveId(entry: null, type, normalizedKey).ToString();
        }

        var (entry, resolved) = await ResolveEntryAndContract(contract, ct).ConfigureAwait(false);
        return ResolveId(entry, resolved, normalizedKey).ToString();
    }

    /// <summary>Dispatches a neuron method call through the configured <see cref="IRuntimeInvocationDispatcher"/>.</summary>
    public async Task<JsonElement?> Call(string brainId, string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var dispatcher = _dispatcher ?? throw new NotSupportedException("This surface has no IRuntimeInvocationDispatcher.");
        var type = LoadedContracts.Find(contract, _assemblies);
        var arranged = Arrange(type, method, arguments);

        var invocation = new RuntimeInvocation(
            Contract: contract,
            Key: key,
            Method: method,
            Arguments: arranged.Arguments,
            Behavior: string.Empty);

        return await dispatcher.DispatchAsync(brainId, invocation, ct).ConfigureAwait(false);
    }

    /// <summary>Invokes a neuron method through the configured <see cref="INeuronProxies"/>.</summary>
    public async Task<JsonElement?> Call(string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        key = NormalizeBrainPlaceholder(key);
        var (entry, type) = await ResolveEntryAndContract(contract, ct).ConfigureAwait(false);
        var call = Arrange(type, method, arguments);
        var values = NeuronBinder.DeserializeArguments(call, ct);

        object? returned;
        try
        {
            returned = call.Method.Invoke(Neuron(entry, type, key), values);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }

        if (returned is not Task task)
        {
            throw new NotSupportedException($"{type.Name}.{call.Method.Name} does not return a Task.");
        }

        var result = await TaskBoxing.ReturnResultAsync(task, call.Method.ReturnType).ConfigureAwait(false);
        if (!call.Method.ReturnType.IsGenericType)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(result, call.Method.ReturnType.GetGenericArguments()[0], Json);
    }

    /// <summary>Resolves the typed neuron proxy reference for <paramref name="contract"/> and <paramref name="key"/>.</summary>
    public async Task<object> Reference(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var (entry, type) = await ResolveEntryAndContract(contract, ct).ConfigureAwait(false);
        return Neuron(entry, type, NormalizeBrainPlaceholder(key));
    }

    /// <summary>Resolves the runtime <see cref="NeuronId"/> for a plain neuron contract and key, or <see langword="null"/> if not a plain contract.</summary>
    public async Task<NeuronId?> PlainId(string contract, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalizedKey = NormalizeBrainPlaceholder(key);
        var (entry, type) = await ResolveEntryAndContract(contract, ct).ConfigureAwait(false);
        return PlainNeuronContracts.Is(type) ? ResolveId(entry, type, normalizedKey) : null;
    }

    /// <summary>Resolves a contract alias or name to its CLR interface when registered in this brain, or <see langword="null"/> when unknown.</summary>
    public async Task<Type?> TryResolveContract(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return (await ResolveEntryAndContract(name, ct).ConfigureAwait(false)).Contract;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    private static ArrangedCall Arrange(Type contract, string method, string arguments)
    {
        try
        {
            using var parsed = string.IsNullOrWhiteSpace(arguments) ? null : JsonDocument.Parse(arguments);
            return NeuronBinder.Arrange(contract, method, parsed is null ? default : parsed.RootElement);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("arguments must be a JSON object or array: " + error.Message, nameof(arguments), error);
        }
        catch (BindingException error)
        {
            throw new ArgumentException(error.Message, nameof(arguments), error);
        }
    }

    private string NormalizeBrainPlaceholder(string key)
    {
        if (!key.Contains("{brain}", StringComparison.OrdinalIgnoreCase) && !key.Contains("{brainScope}", StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        var currentBrain = _callers?.Current?.Brain.Value;
        if (string.IsNullOrWhiteSpace(currentBrain))
        {
            return key;
        }

        return key
            .Replace("{brain}", currentBrain, StringComparison.OrdinalIgnoreCase)
            .Replace("{brainScope}", currentBrain, StringComparison.OrdinalIgnoreCase);
    }

    private IBrainObserver RequireObserver()
        => _observer ?? throw new NotSupportedException("This host registers no IBrainObserver.");

    private async Task<Dictionary<string, BrainType>> Neurons(CancellationToken ct)
        => (await RequireObserver().Types(ct).ConfigureAwait(false))
            .Where(static entry => entry.Kind == TypeKind.Neuron)
            .ToDictionary(static entry => entry.Name, StringComparer.Ordinal);

    private BrainType Entry(IReadOnlyDictionary<string, BrainType> neurons, string name)
    {
        var canonical = CurrentName(name);
        if (neurons.TryGetValue(canonical, out var exact))
        {
            if (!Allowed(exact.Name))
            {
                throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list.");
            }

            return exact;
        }

        var match = neurons.Values.FirstOrDefault(entry =>
            string.Equals(entry.Alias, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Name, canonical, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)
            || entry.Name.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)
            || (entry.Name.StartsWith('I') && entry.Name[1..].Equals(name, StringComparison.OrdinalIgnoreCase)));

        if (match is not null)
        {
            if (!Allowed(match.Name))
            {
                throw new ArgumentException($"{name} is not a contract this app composes. Pass a full type name from neurons_list.");
            }

            return match;
        }

        throw new ArgumentException($"{name} is not a neuron type of this brain. Pass the contract alias or full type name.");
    }

    private async Task<(BrainType Entry, Type Contract)> ResolveEntryAndContract(string name, CancellationToken ct)
    {
        var entry = Entry(await Neurons(ct).ConfigureAwait(false), name);
        if (LoadedContracts.TryFind(entry.Name, _assemblies) is { } direct)
        {
            return (entry, direct);
        }

        // Screen neuron types (e.g. app.*) expose IScreen as their contract interface even when the host carries contracts only.
        if (entry.Implements.Contains(typeof(IScreen).FullName, StringComparer.Ordinal))
        {
            return (entry, typeof(IScreen));
        }

        return (entry, LoadedContracts.Find(entry.Name, _assemblies));
    }

    private object Neuron(BrainType entry, Type contract, string key)
    {
        if (_proxies is null)
        {
            throw new NotSupportedException("This host cannot reach neurons: it registers no INeuronProxies.");
        }

        try
        {
            return GetNeuron.MakeGenericMethod(contract).Invoke(_proxies, [ResolveId(entry, contract, key)])!;
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.Message}", error);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw new ArgumentException($"{contract.Name} cannot address '{key}': {error.InnerException.Message}", error.InnerException);
        }
    }

    private static NeuronId ResolveId(BrainType? entry, Type contract, string fullKey)
    {
        if (contract == typeof(IScreen))
        {
            if (NeuronId.TryParse(fullKey, out var screenId))
            {
                return screenId;
            }

            if (entry is not null
                && !string.IsNullOrWhiteSpace(entry.Alias)
                && !string.Equals(entry.Alias, "screen", StringComparison.Ordinal))
            {
                var brain = NeuronAddress.ScopeOf(fullKey)
                    ?? throw new ArgumentException($"'{fullKey}' names no brain: a plain neuron key is {{brain}}/{{key}}.", nameof(fullKey));
                return new NeuronId(new BrainId(brain), entry.Alias, fullKey[(brain.Length + 1)..]);
            }
        }

        return PlainNeuronContracts.IdOf(contract, fullKey);
    }
}
