using System.Text.Json;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>A resolved Orleans <see cref="INeuronHost"/> grain and, when indexed in-process, its concrete neuron class.</summary>
/// <param name="Host">The Orleans host grain reference.</param>
/// <param name="NeuronClass">The concrete neuron class when known in-process; otherwise <see langword="null"/>.</param>
public readonly record struct ResolvedNeuronHost(INeuronHost Host, Type? NeuronClass);

/// <summary>
/// Unified local Orleans host-grain resolution and interleave-aware dispatch for plain neurons.
/// Uses the in-process <see cref="IHostedNeuronTypes"/> and <see cref="CallPolicy.MayInterleave"/> fast path when the
/// neuron class is indexed locally, avoiding extra <see cref="INeuronHostCatalog.HostType"/> and
/// <see cref="INeuronHost.IsInterleaved"/> grain RPC hops, and falls back cleanly when unindexed.
/// </summary>
public sealed class OrleansNeuronDispatcher(IGrainFactory grains, IHostedNeuronTypes? types = null)
{
    private readonly IGrainFactory _grains = grains ?? throw new ArgumentNullException(nameof(grains));
    private readonly IHostedNeuronTypes? _types = types;

    /// <summary>
    /// Resolves the <see cref="INeuronHost"/> grain for <paramref name="target"/> using the in-process type registry first,
    /// falling back to <paramref name="localHostTypeLookup"/> or <see cref="INeuronHostCatalog.HostType"/> when unindexed.
    /// </summary>
    public ValueTask<ResolvedNeuronHost> ResolveAsync(
        NeuronId target,
        bool requireHosted = false,
        Func<string, string?>? localHostTypeLookup = null,
        bool continueOnCapturedContext = false,
        CancellationToken cancellationToken = default)
    {
        var key = target.ToString();
        if (_types is not null)
        {
            if (_types.TryGetNeuronClass(target.Type, out var neuronClass) && neuronClass is not null)
            {
                var grainType = NeuronTypeRegistry.GrainTypeOf(NeuronTypeRegistry.PolicyOf(neuronClass));
                return ValueTask.FromResult(new ResolvedNeuronHost(_grains.GetGrain<INeuronHost>(GrainId.Create(grainType, key)), neuronClass));
            }

            if (!requireHosted)
            {
                return ValueTask.FromResult(new ResolvedNeuronHost(_grains.GetGrain<INeuronHost>(GrainId.Create(NeuronTypeRegistry.AffineGrainType, key)), null));
            }
        }

        if (localHostTypeLookup?.Invoke(target.Type) is { } localHostType)
        {
            return ValueTask.FromResult(new ResolvedNeuronHost(_grains.GetGrain<INeuronHost>(GrainId.Create(localHostType, key)), null));
        }

        return ResolveFromCatalogAsync(target.Type, key, requireHosted, continueOnCapturedContext, cancellationToken);
    }

    /// <summary>Resolves <paramref name="target"/> against an explicit host grain type.</summary>
    public ResolvedNeuronHost ResolveFixedHost(NeuronId target, string hostType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostType);
        Type? neuronClass = null;
        _ = _types?.TryGetNeuronClass(target.Type, out neuronClass);
        return new ResolvedNeuronHost(_grains.GetGrain<INeuronHost>(GrainId.Create(hostType, target.ToString())), neuronClass);
    }

    /// <summary>Dispatches a request-response invocation to a previously resolved host grain.</summary>
    public static async Task<JsonElement?> InvokeAsync(
        ResolvedNeuronHost resolved,
        RuntimeInvocation call,
        bool continueOnCapturedContext = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var interleaved = resolved.NeuronClass is { } neuronClass
            ? CallPolicy.MayInterleave(neuronClass, NeuronHostGrainBase.Bind(neuronClass, call), call)
            : await resolved.Host.IsInterleaved(call).WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext);
        return await (interleaved
            ? resolved.Host.InvokeInterleaved(call, cancellationToken)
            : resolved.Host.Invoke(call, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Dispatches a one-way invocation to a previously resolved host grain.</summary>
    public static async Task PostAsync(
        ResolvedNeuronHost resolved,
        RuntimeInvocation call,
        bool continueOnCapturedContext = false)
    {
        ArgumentNullException.ThrowIfNull(call);
        var interleaved = resolved.NeuronClass is { } neuronClass
            ? CallPolicy.MayInterleave(neuronClass, NeuronHostGrainBase.Bind(neuronClass, call), call)
            : await resolved.Host.IsInterleaved(call).ConfigureAwait(continueOnCapturedContext);
        await (interleaved
            ? resolved.Host.PostInterleaved(call)
            : resolved.Host.Post(call)).ConfigureAwait(false);
    }

    /// <summary>Resolves <paramref name="target"/> and dispatches a request-response invocation.</summary>
    public async Task<JsonElement?> InvokeAsync(
        NeuronId target,
        RuntimeInvocation call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var resolved = await ResolveAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await InvokeAsync(resolved, call, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves <paramref name="target"/> and dispatches a one-way invocation.</summary>
    public async Task PostAsync(NeuronId target, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var resolved = await ResolveAsync(target, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await PostAsync(resolved, call).ConfigureAwait(false);
    }

    private async ValueTask<ResolvedNeuronHost> ResolveFromCatalogAsync(
        string neuronType,
        string key,
        bool requireHosted,
        bool continueOnCapturedContext,
        CancellationToken cancellationToken)
    {
        var grainType = await _grains.GetGrain<INeuronHostCatalog>("/").HostType(neuronType).WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext);
        if (grainType is null)
        {
            if (requireHosted)
            {
                throw new InvalidOperationException($"No neuron class is hosted for type '{neuronType}'.");
            }

            grainType = NeuronTypeRegistry.AffineGrainType;
        }

        return new ResolvedNeuronHost(_grains.GetGrain<INeuronHost>(GrainId.Create(grainType, key)), null);
    }
}
