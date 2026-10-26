using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition;


// Transitional (06-02, R4-12, deleted in 06-08 with the Framework): typed calls between migrated and unmigrated neurons while
// families move one at a time. A call through a contract that is an Orleans grain interface of a legacy class goes to that grain
// (GetGrain by the contract, keyed "{brain}/{key}"); any other call goes to the runtime host grain of the plain [Neuron] class
// of the target's type. Both directions carry the caller's neuron id in the runtime's provenance slot (RequestContext
// "db.caller", which host grains read as Context.Caller and legacy grains read through LegacyNeuronCalls.CallerOf). Only
// contracts routed through LegacyNeuronCalls reach this sink, and the composition is the only owner (T-06-42).
public sealed class LegacyGrainSink(IGrainFactory grains, string caller, Func<global::DigitalBrain.NeuronId, global::DigitalBrain.Invocation.RuntimeInvocation, Task>? streamAccess = null) : IInvocationSink
{
    // The runtime's provenance key (DigitalBrain.Runtime.Orleans HostNeuronContext.CallerKey), read by every host grain.
    public const string CallerKey = "db.caller";

    private static readonly ConcurrentDictionary<string, Type> Contracts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> HostTypes = new(StringComparer.Ordinal);
    private static readonly MethodInfo StreamItems = typeof(LegacyGrainSink).GetMethod(nameof(Items), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static void Register(Type contract) => Contracts.TryAdd(NeuronProxy.ContractName(contract), contract);

    public async Task<JsonElement?> Invoke(global::DigitalBrain.NeuronId target, global::DigitalBrain.Invocation.RuntimeInvocation call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        Stamp();
        if (Legacy(target, call) is not { } legacy) { return await Host(target).Invoke(Stamped(call), cancellationToken).ConfigureAwait(false); }
        var (grain, method, arguments) = Bind(legacy.Grain, legacy.Contract, call, cancellationToken);
        var returned = method.Invoke(grain, arguments);
        switch (returned)
        {
            case Task task:
                await task.ConfigureAwait(false);
                var type = method.ReturnType;
                return type.IsGenericType ? JsonSerializer.SerializeToElement(type.GetProperty(nameof(Task<int>.Result))!.GetValue(task), type.GetGenericArguments()[0], global::DigitalBrain.Invocation.RuntimeProtocol.Json) : null;
            case null:
                return null;
            default:
                throw new NotSupportedException($"{legacy.Contract.Name}.{method.Name} returns {method.ReturnType.Name}; legacy calls return Task or Task<T>.");
        }
    }

    public async Task Post(global::DigitalBrain.NeuronId target, global::DigitalBrain.Invocation.RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        Stamp();
        if (Legacy(target, call) is null) { await Host(target).Post(Stamped(call)).ConfigureAwait(false); return; }
        _ = await Invoke(target, call, CancellationToken.None).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonElement> Stream(global::DigitalBrain.NeuronId target, global::DigitalBrain.Invocation.RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        Stamp();
        IAsyncEnumerable<JsonElement> items;
        if (Legacy(target, call) is not { } legacy)
        {
            // A host grain's stream passes no incoming filter, so the os access policy is asked before it starts (06-06d).
            if (streamAccess is not null) { await streamAccess(target, call).ConfigureAwait(false); }
            items = Host(target).Stream(Stamped(call), cancellationToken);
        }
        else
        {
            var (grain, method, arguments) = Bind(legacy.Grain, legacy.Contract, call, cancellationToken);
            var type = method.ReturnType;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IAsyncEnumerable<>))
            { throw new NotSupportedException($"{legacy.Contract.Name}.{method.Name} does not stream."); }
            items = (IAsyncEnumerable<JsonElement>)StreamItems.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [method.Invoke(grain, arguments), cancellationToken])!;
        }
        await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false)) { yield return item; }
    }

    private static async IAsyncEnumerable<JsonElement> Items<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        { yield return JsonSerializer.SerializeToElement(item, global::DigitalBrain.Invocation.RuntimeProtocol.Json); }
    }

    // A legacy target: the contract is a grain interface whose grain for "{brain}/{key}" has the target's type.
    private (IAddressable Grain, Type Contract)? Legacy(global::DigitalBrain.NeuronId target, global::DigitalBrain.Invocation.RuntimeInvocation call)
    {
        // Plain contracts never derive a grain interface (D-11), so the contract alone decides the runtime.
        if (ContractOf(call.Contract) is not { } contract || !typeof(IGrainWithStringKey).IsAssignableFrom(contract)) { return null; }
        var grain = grains.GetGrain(contract, $"{target.Brain.Value}/{target.Key}");
        var type = grain.GetGrainId().Type.ToString();
        if (!string.Equals(type, target.Type, StringComparison.Ordinal))
        { throw new InvalidOperationException($"{contract.Name} resolves to grain type '{type}', not '{target.Type}' of {target}."); }
        return (grain, contract);
    }

    // A contract by its wire name: one registered, else a loaded interface of that full name, else a loaded interface whose
    // [NeuronAlias] it is. The alias step matters for the access policy: a neuron's own Get never registers its contract, so
    // without it a module-internal contract read the owner rule in a process where no IPlainNeurons.Get named it first.
    internal static Type? ContractOf(string name)
    {
        if (Contracts.TryGetValue(name, out var known)) { return known; }
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();
        var found = assemblies.Select(assembly => assembly.GetType(name, throwOnError: false)).FirstOrDefault(type => type is { IsInterface: true })
            ?? assemblies.Where(References)
                .SelectMany(Loadable).FirstOrDefault(type => type.IsInterface
                    && type.GetCustomAttribute<global::DigitalBrain.Contracts.NeuronAliasAttribute>(inherit: false)?.Alias == name);
        if (found is not null) { Contracts.TryAdd(name, found); }
        return found;
    }

    // Only assemblies that can declare a contract (they reference DigitalBrain) are scanned for aliases.
    private static bool References(Assembly assembly)
        => assembly.GetReferencedAssemblies().Any(reference => reference.Name == typeof(global::DigitalBrain.Contracts.NeuronAliasAttribute).Assembly.GetName().Name);

    private static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
    }

    private INeuronHost Host(global::DigitalBrain.NeuronId target)
    {
        var grainType = HostTypeOf(target.Type) ?? throw new InvalidOperationException($"No neuron class is hosted for type '{target.Type}', and the call's contract is not a legacy grain interface.");
        return grains.GetGrain<INeuronHost>(GrainId.Create(grainType, target.ToString()));
    }

    // The host grain of the plain [Neuron] class of a type, by the runtime's policy (NeuronTypeRegistry.PolicyOf); null when no
    // loaded class has that type.
    private static string? HostTypeOf(string type) => HostTypes.GetOrAdd(type, static name =>
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = [.. error.Types.OfType<Type>()]; }
            foreach (var candidate in types)
            {
                if (candidate.IsAbstract || !typeof(global::DigitalBrain.Neuron).IsAssignableFrom(candidate)) { continue; }
                if (candidate.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type != name) { continue; }
                if (candidate.IsDefined(typeof(StatelessNeuronAttribute), inherit: false)) { return "neuron.host.stateless"; }
                return candidate.IsDefined(typeof(ReentrantAttribute), inherit: true) ? "neuron.host.reentrant" : "neuron.host";
            }
        }
        return null;
    });

    private static (object Grain, MethodInfo Method, object?[] Arguments) Bind(IAddressable grain, Type contract, global::DigitalBrain.Invocation.RuntimeInvocation call, CancellationToken cancellationToken)
    {
        MethodInfo[] candidates = [.. new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(face => face.GetMethods())
            .Where(method => method.Name == call.Method && method.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken)) == call.Arguments.Length)];
        var method = candidates.Length == 1 ? candidates[0]
            : throw new InvalidOperationException($"{contract.Name}.{call.Method} with {call.Arguments.Length} arguments matches {candidates.Length} methods.");
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (int index = 0, argument = 0; index < parameters.Length; index++)
        {
            arguments[index] = parameters[index].ParameterType == typeof(CancellationToken)
                ? cancellationToken
                : call.Arguments[argument++].Deserialize(parameters[index].ParameterType, global::DigitalBrain.Invocation.RuntimeProtocol.Json);
        }
        return (grain, method, arguments);
    }

    private global::DigitalBrain.Invocation.RuntimeInvocation Stamped(global::DigitalBrain.Invocation.RuntimeInvocation call) => string.Equals(call.Behavior, caller, StringComparison.Ordinal) ? call : call with { Behavior = caller };

    // Called inside the async call methods, so the RequestContext change stays inside the call (as the runtime's sink does).
    private void Stamp() => RequestContext.Set(CallerKey, caller);
}
