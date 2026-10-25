using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Spi;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// What the three host grains share: parse the key <c>brain/type/key</c>, resolve the plain class, build and attach it, then
/// dispatch each <see cref="RuntimeInvocation"/> through the one <see cref="NeuronBinder"/>. The invocation's Behavior is ignored:
/// provenance comes from RequestContext, stamped by <see cref="OrleansInvocationSink"/>.
/// </summary>
internal abstract class NeuronHostGrainBase(HostedNeuronTypes types, IGrainFactory grains) : Grain, INeuronHost
{
    /// <summary>The binder every host grain dispatches through; its cache also serves the interleaving predicate.</summary>
    internal static readonly NeuronBinder Binder = new();

    private static readonly ConcurrentDictionary<(Type Class, string Contract), Type?> Contracts = new();

    private readonly Queue<Func<Task>> _deferred = new();
    private Neuron? _neuron;
    private Type? _class;
    private string _key = string.Empty;

    /// <summary>The host grain type this class implements.</summary>
    protected abstract string GrainTypeName { get; }

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _key = this.GetPrimaryKeyString();
        if (!NeuronId.TryParse(_key, out var id)) { throw new BindingException($"'{_key}' is not a neuron id brain/type/key."); }
        if (!types.TryGet(id.Type, out var neuronClass)) { throw new BindingException($"No neuron class is hosted for type '{id.Type}'."); }
        var expected = HostedNeuronTypes.GrainTypeOf(HostedNeuronTypes.PolicyOf(neuronClass));
        if (expected != GrainTypeName) { throw new BindingException($"{neuronClass.FullName} is hosted by {expected}, not {GrainTypeName}."); }
        _class = neuronClass;
        var neuron = (Neuron)ActivatorUtilities.CreateInstance(ServiceProvider, neuronClass);
        NeuronAttachment.Attach(neuron, new HostedNeuronContext(id, ServiceProvider, grains, types, ServiceProvider.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToString(), _deferred.Enqueue));
        await NeuronAttachment.Activate(neuron, cancellationToken).ConfigureAwait(true);
        _neuron = neuron;
    }

    /// <inheritdoc />
    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (_neuron is { } neuron)
        {
            var cause = reason.ReasonCode switch
            {
                DeactivationReasonCode.ActivationIdle => DeactivationCause.Idle,
                DeactivationReasonCode.ShuttingDown => DeactivationCause.Shutdown,
                DeactivationReasonCode.ApplicationError or DeactivationReasonCode.ActivationFailed => DeactivationCause.Failed,
                _ => DeactivationCause.Requested,
            };
            await NeuronAttachment.Deactivate(neuron, cause, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default)
    {
        var (neuron, bound) = Prepare(call);
        try { return await Binder.InvokeAsync(neuron, bound, call.Arguments, cancellationToken).ConfigureAwait(true); }
        finally { await Drain().ConfigureAwait(true); }
    }

    /// <inheritdoc />
    public async Task Post(RuntimeInvocation call)
    {
        var (neuron, bound) = Prepare(call);
        try { _ = await Binder.InvokeAsync(neuron, bound, call.Arguments, CancellationToken.None).ConfigureAwait(true); }
        finally { await Drain().ConfigureAwait(true); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (neuron, bound) = Prepare(call);
        await foreach (var item in Binder.StreamAsync(neuron, bound, call.Arguments, cancellationToken).ConfigureAwait(true)) { yield return item; }
    }

    /// <summary>The contract <paramref name="contract"/> names on <paramref name="neuronClass"/>, or null when the class does not implement it.</summary>
    internal static Type? ContractOf(Type neuronClass, string contract)
        => Contracts.GetOrAdd((neuronClass, contract), static key => key.Class.GetInterfaces().FirstOrDefault(face => NeuronProxy.ContractName(face) == key.Contract));

    /// <summary>Binds <paramref name="call"/> against the contract it names on <paramref name="neuronClass"/>.</summary>
    /// <exception cref="BindingException">The class does not implement the contract, or the call does not bind.</exception>
    internal static BoundCall Bind(Type neuronClass, RuntimeInvocation call)
    {
        var contract = ContractOf(neuronClass, call.Contract) ?? throw new BindingException($"{neuronClass.FullName} does not implement contract {call.Contract}.");
        return Binder.Bind(contract, call);
    }

    private (Neuron Neuron, BoundCall Call) Prepare(RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (_neuron is not { } neuron || _class is null) { throw new InvalidOperationException($"The neuron {_key} is not active."); }
        // The grain key decides brain, type and key; an invocation naming any other neuron is refused before neuron code runs.
        if (!string.Equals(call.Key, _key, StringComparison.Ordinal)) { throw new BindingException($"The call names {call.Key}, but this host runs {_key}."); }
        return (neuron, Bind(_class, call));
    }

    private async Task Drain()
    {
        while (_deferred.TryDequeue(out var work)) { await work().ConfigureAwait(true); }
    }
}
