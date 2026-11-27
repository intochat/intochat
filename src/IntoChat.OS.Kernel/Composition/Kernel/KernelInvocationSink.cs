using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Kernel.Composition;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition.Kernel;


// The os side's calls into DigitalBrain plain neurons (06-08): every call goes to the runtime host grain of the [Neuron] class of
// the target's type, carrying the caller's neuron id in the runtime's provenance slot (RequestContext "db.caller", which host
// grains read as Context.Caller). The kernel composition owns it (IPlainNeurons, IPlainSignals).
public sealed class KernelInvocationSink(
    IGrainFactory grains,
    string caller,
    Func<DigitalBrain.NeuronId, DigitalBrain.Invocation.RuntimeInvocation, Task>? streamAccess = null,
    EdgeAuthority? edge = null) : IInvocationSink
{
    // The runtime's provenance key (DigitalBrain.Runtime.Orleans HostNeuronContext.CallerKey), read by every host grain.
    public const string CallerKey = "db.caller";

    private static readonly ConcurrentDictionary<string, Type> Contracts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> HostTypes = new(StringComparer.Ordinal);

    // A sink over the grain factory of services (a silo or a cluster client), calling as caller.
    public static KernelInvocationSink For(IServiceProvider services, string caller)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IGrainFactory>(services),
            caller,
            edge: services.GetService(typeof(EdgeAuthority)) as EdgeAuthority);
    }

    internal static void Register(Type contract) => Contracts.TryAdd(NeuronProxy.ContractName(contract), contract);

    public async Task<JsonElement?> Invoke(DigitalBrain.NeuronId target, DigitalBrain.Invocation.RuntimeInvocation call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var _ = edge?.EnterInProcess();
        // Preserve the grain scheduler until dispatch: source-origin guards use Orleans' physical caller identity.
        var host = await Host(target, cancellationToken).ConfigureAwait(true);
        Stamp();
        var stamped = Stamped(call);
        var interleaved = await host.IsInterleaved(stamped).WaitAsync(cancellationToken).ConfigureAwait(true);
        return await (interleaved ? host.InvokeInterleaved(stamped, cancellationToken) : host.Invoke(stamped, cancellationToken)).ConfigureAwait(false);
    }

    public async Task Post(DigitalBrain.NeuronId target, DigitalBrain.Invocation.RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var _ = edge?.EnterInProcess();
        var host = await Host(target, CancellationToken.None).ConfigureAwait(true);
        Stamp();
        var stamped = Stamped(call);
        if (await host.IsInterleaved(stamped).ConfigureAwait(true)) { await host.PostInterleaved(stamped).ConfigureAwait(false); }
        else { await host.Post(stamped).ConfigureAwait(false); }
    }

    public async IAsyncEnumerable<JsonElement> Stream(DigitalBrain.NeuronId target, DigitalBrain.Invocation.RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var _ = edge?.EnterInProcess();
        Stamp();
        // A host grain's stream passes no incoming filter, so the os access policy is asked before it starts (06-06d).
        if (streamAccess is not null) { await streamAccess(target, call).ConfigureAwait(true); }
        var host = await Host(target, cancellationToken).ConfigureAwait(true);
        await foreach (var item in host.Stream(Stamped(call), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(true)) { yield return item; }
    }

    // A contract by its wire name: one registered, else a loaded interface of that full name or [NeuronAlias] via LoadedContracts.
    internal static Type? ContractOf(string name)
    {
        if (Contracts.TryGetValue(name, out var known)) { return known; }
        var found = DigitalBrain.Runtime.Http.Surface.LoadedContracts.TryFindInterface(name);
        if (found is not null) { Contracts.TryAdd(name, found); }
        return found;
    }

    private async Task<INeuronHost> Host(DigitalBrain.NeuronId target, CancellationToken cancellationToken)
    {
        // Cluster clients carry contracts without implementation classes. Only the serving kernel can answer their host
        // policy; a guessed affine host would address reentrant and stateless neurons incorrectly.
        var grainType = HostTypeOf(target.Type)
            ?? await grains.GetGrain<ICompiledTypeTable>(TypeTableKey.NoBrain).HostType(target.Type).WaitAsync(cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException($"No neuron class is hosted for type '{target.Type}'.");
        return grains.GetGrain<INeuronHost>(GrainId.Create(grainType, target.ToString()));
    }

    // The host grain of the plain [Neuron] class of a type, by the runtime's policy (NeuronTypeRegistry.PolicyOf); null when no
    // loaded class has that type.
    internal static string? HostTypeOfLoaded(string type) => HostTypeOf(type);

    private static string? HostTypeOf(string type) => HostTypes.GetOrAdd(type, static name =>
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { types = [.. error.Types.OfType<Type>()]; }
            foreach (var candidate in types)
            {
                if (candidate.IsAbstract || !typeof(Neuron).IsAssignableFrom(candidate)) { continue; }
                if (candidate.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type != name) { continue; }
                if (candidate.IsDefined(typeof(StatelessNeuronAttribute), inherit: false)) { return "neuron.host.stateless"; }
                return candidate.IsDefined(typeof(ReentrantAttribute), inherit: true) ? "neuron.host.reentrant" : "neuron.host";
            }
        }
        return null;
    });

    private DigitalBrain.Invocation.RuntimeInvocation Stamped(DigitalBrain.Invocation.RuntimeInvocation call) => string.Equals(call.Behavior, caller, StringComparison.Ordinal) ? call : call with { Behavior = caller };

    // Called inside the async call methods, so the RequestContext change stays inside the call (as the runtime's sink does).
    private void Stamp() => RequestContext.Set(CallerKey, caller);
}
