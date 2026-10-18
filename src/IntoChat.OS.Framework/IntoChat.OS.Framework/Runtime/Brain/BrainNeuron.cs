using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Framework;

// The brain's first neuron: its state, its creation, its lifecycle signals.
[GrainType("brain")]
internal sealed class BrainNeuron(
    [PersistentState("brain", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<State> store,
    IEnumerable<ICreationHandler> creation,
    ILogger<BrainNeuron> logger) : Neuron<State>(store), IBrain, IKernelAnnouncer
{
    public Task Announce(Signal signal) => PublishAsync(signal);

    public Task<State> Read()
        => Task.FromResult(Exists ? Snapshot : throw new InvalidOperationException("This brain has not been created."));

    public async Task Create(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireCreator(owner);
        if (Exists) { return; }
        var created = new Created(owner);
        await Save(new State(owner, DateTimeOffset.UtcNow), created);
        foreach (var handler in creation)
        {
            // One handler failing must not fail the brain; Activated handlers catch up later.
            try { await handler.Created(this, created, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Creation handler {Handler} failed for brain {Brain}.", handler.GetType().Name, this.GetPrimaryKeyString()); }
        }
        await PublishAsync(new Activated(owner));
    }

    internal Task Woke() => Exists ? PublishAsync(new Activated(Snapshot.Owner)) : Task.CompletedTask;

    // Creating is the platform's act, or the owner's own. Host code with no stamped caller is trusted.
    private static void RequireCreator(Owner owner)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return; }
        if (caller.Kind == CallerKind.Platform || caller.AccountId == owner.AccountId) { return; }
        throw new UnauthorizedAccessException("Only the platform or the owner may create a brain.");
    }
}

// Publishes Activated on every wake of a created brain.
internal sealed class BrainActivation : IActivationHandler<BrainNeuron>
{
    public Task Activated(BrainNeuron neuron, CancellationToken cancellationToken) => neuron.Woke();
}
