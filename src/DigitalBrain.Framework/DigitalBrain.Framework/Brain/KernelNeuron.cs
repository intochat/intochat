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
    // Storage hands back an uninitialized record before the first write, so existence is the record's, not the object's.
    internal bool IsCreated => store.RecordExists;

    // The kernel's key is the brain id itself.
    public override NeuronAccess Access(string operation) => new(this.GetPrimaryKeyString());

    public Task<State> Read()
        => Task.FromResult(IsCreated ? store.State : throw new InvalidOperationException("This brain has not been created."));

    public async Task Create(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireCreator(owner);
        if (IsCreated) { return; }
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

    internal Task Woke() => IsCreated ? PublishAsync(new Activated(store.State.Owner)) : Task.CompletedTask;

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
