using DigitalBrain.State;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Composition;

// The brain's first neuron (06-08): its state, its creation and its lifecycle signals, a plain neuron of the kernel composition
// keyed {brain}/main. It also publishes the kernel's instance and type signals, so they arrive with the brain as publisher.
// The state is a clean break from the grain's "brain" record (R4-24): an existing brain is created again on its next access.
[DigitalBrain.Neuron(IBrain.Type, State = "brain")]
internal sealed class BrainNeuron(IEnumerable<ICreationHandler> creation, ILogger<BrainNeuron> logger)
    : DigitalBrain.Neuron<BrainStorage>, IBrain, IBrainAnnouncer
{
    private string Brain => Id.Brain.Value;

    public Task<State> Read()
        => State.Owner is { } owner ? Task.FromResult(new State(owner, State.CreatedAt))
            : throw new InvalidOperationException("This brain has not been created.");

    public async Task Create(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireCreator(owner);
        if (State.Owner is not null) { return; }
        var created = new Created(owner);
        await Save(new BrainStorage { Owner = owner, CreatedAt = DateTimeOffset.UtcNow }, created);
        foreach (var handler in creation)
        {
            // One handler failing must not fail the brain; Activated handlers catch up later.
            try { await handler.Created(Brain, created, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Creation handler {Handler} failed for brain {Brain}.", handler.GetType().Name, Brain); }
        }
        await Publish(new Activated(owner));
    }

    // Every wake of a created brain says so; a brain that does not exist yet stays silent.
    protected override Task OnActivate(CancellationToken cancellationToken)
        => State.Owner is { } owner ? Publish(new Activated(owner)) : Task.CompletedTask;

    // The kernel's own grains announce; a stamped caller is someone else and is refused.
    public Task InstanceCreated(string type, string key) => Announce(new InstanceCreated(type, key));

    public Task InstanceRetired(string type, string key) => Announce(new InstanceRetired(type, key));

    public Task TypeAdded(string name) => Announce(new TypeAdded(name));

    private Task Announce(DigitalBrain.Signal signal)
    {
        if (CallerContextStamper.TryGet(out _)) { throw new UnauthorizedAccessException("Only the kernel announces a brain's instances and types."); }
        return Publish(signal);
    }

    // Creating is the platform's act, or the owner's own. Host code with no stamped caller is trusted.
    private static void RequireCreator(Owner owner)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return; }
        if (caller.Kind == CallerKind.Platform || caller.AccountId == owner.AccountId) { return; }
        throw new UnauthorizedAccessException("Only the platform or the owner may create a brain.");
    }
}

// The kernel grains' way into the brain neuron (06-08; it replaces the grain's IKernelAnnouncer): one-way and unstamped, since the
// brain neuron's own first write is registered through the same directory that announces it.
[DigitalBrain.Contracts.NeuronAlias("kernel.announcer"), DigitalBrain.Contracts.DefaultNeuronType(IBrain.Type), DigitalBrain.Access.Internal]
public interface IBrainAnnouncer
{
    [DigitalBrain.OneWayCall] Task InstanceCreated(string type, string key);
    [DigitalBrain.OneWayCall] Task InstanceRetired(string type, string key);
    [DigitalBrain.OneWayCall] Task TypeAdded(string name);
}

// What a brain persists (06-08 clean break from the grain's State record): whose it is and when it came to be.
[ExportableState]
internal sealed record BrainStorage
{
    public Owner? Owner { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
