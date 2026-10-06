using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Framework;

/// <summary>The brain's first neuron: its state, its creation, its lifecycle signals.</summary>
[GrainType("kernel")]
internal sealed class KernelNeuron(
    [PersistentState("kernel", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<State> store,
    IEnumerable<ICreationHandler> creation,
    ILogger<KernelNeuron> logger) : Neuron, IKernel
{
    internal bool IsCreated => store.State is not null;

    public Task<State> Read()
        => Task.FromResult(store.State ?? throw new InvalidOperationException("This brain has not been created."));

    public async Task Create(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireCreator(owner);
        if (store.State is not null) { return; }
        store.State = new State(owner, DateTimeOffset.UtcNow);
        await store.WriteStateAsync();
        var created = new Created(owner);
        await PublishAsync(created);
        foreach (var handler in creation)
        {
            // One handler failing must not fail the brain; Activated handlers catch up later.
            try { await handler.Created(this, created, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Creation handler {Handler} failed for brain {Brain}.", handler.GetType().Name, this.GetPrimaryKeyString()); }
        }
        await PublishAsync(new Activated(owner));
    }

    internal Task Woke() => store.State is { } state ? PublishAsync(new Activated(state.Owner)) : Task.CompletedTask;

    // Creating is the platform's act, or the owner's own. Host code with no stamped caller is trusted.
    private static void RequireCreator(Owner owner)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return; }
        if (caller.Kind == CallerKind.Platform || caller.AccountId == owner.AccountId) { return; }
        throw new UnauthorizedAccessException("Only the platform or the owner may create a brain.");
    }
}

/// <summary>Publishes <see cref="Activated"/> on every wake of a created brain.</summary>
internal sealed class KernelActivation : IActivationHandler<KernelNeuron>
{
    public Task Activated(KernelNeuron neuron, CancellationToken cancellationToken) => neuron.Woke();
}
