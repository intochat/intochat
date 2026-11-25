using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Signals;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Runtime.Orleans.Brain;

/// <summary>
/// The brain's first neuron: its state, its creation and its lifecycle signals, a plain neuron
/// keyed {brain}/main. It also publishes the kernel's instance and type signals, so they arrive with the brain as publisher.
/// </summary>
[Neuron(IBrain.Type, State = "brain")]
public sealed class BrainNeuron
    : Neuron<BrainStorage>, IBrain, IBrainAnnouncer
{
    private readonly IEnumerable<ICreationHandler> creation;
    private readonly ILogger<BrainNeuron> logger;
    private readonly IBrainResidencyStorage? residencyStorage;
    private readonly IBrainResidencyRegistry? residencyRegistry;

    private string BrainId => Id.Brain.Value;

    private static readonly Action<ILogger, string, string, Exception?> CreationHandlerFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(1, nameof(CreationHandlerFailed)), "Creation handler {Handler} failed for brain {Brain}.");

    public BrainNeuron(
        IEnumerable<ICreationHandler> creation,
        ILogger<BrainNeuron> logger,
        IBrainResidencyStorage? residencyStorage = null,
        IBrainResidencyRegistry? residencyRegistry = null)
    {
        this.creation = creation;
        this.logger = logger;
        this.residencyStorage = residencyStorage;
        this.residencyRegistry = residencyRegistry;
    }

    public Task<BrainState> Read()
        => State.Owner is { } owner
            ? Task.FromResult(new BrainState(owner, State.CreatedAt) { Residency = State.Residency ?? new BrainResidency.Persistent(owner.AccountId) })
            : throw new InvalidOperationException("This brain has not been created.");

    public Task Create(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return CreateCore(owner, new BrainResidency.Persistent(owner.AccountId));
    }

    public Task CreateEphemeral(Owner owner, BrainResidency.Ephemeral residency)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(residency);
        return CreateCore(owner, residency);
    }

    public async Task<BrainState> Claim(string ownerPrincipalId, string? accountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        if (ownerPrincipalId.StartsWith("try-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An ephemeral try identity cannot claim a brain into persistent storage.", nameof(ownerPrincipalId));
        }

        RequireClaimer(ownerPrincipalId);

        if (State.Owner is null)
        {
            throw new InvalidOperationException("This brain has not been created.");
        }

        if (State.Residency is BrainResidency.Persistent existing)
        {
            if (string.Equals(existing.OwnerPrincipalId, ownerPrincipalId, StringComparison.Ordinal)
                || string.Equals(State.Owner.AccountId, ownerPrincipalId, StringComparison.Ordinal))
            {
                return new BrainState(State.Owner, State.CreatedAt) { Residency = existing };
            }

            throw new UnauthorizedAccessException("This brain is already claimed by another principal.");
        }

        // Legacy persisted brains without an explicit Residency field are already persistent.
        if (State.Residency is null)
        {
            if (string.Equals(State.Owner.AccountId, ownerPrincipalId, StringComparison.Ordinal))
            {
                return new BrainState(State.Owner, State.CreatedAt) { Residency = new BrainResidency.Persistent(ownerPrincipalId) };
            }

            throw new UnauthorizedAccessException("This brain is already persistent and belongs to another principal.");
        }

        // Flush the in-memory BrainLog into durable encrypted storage in-place before writing the persistent state.
        if (residencyStorage is not null)
        {
            await residencyStorage.PromoteAsync(BrainId, CancellationToken.None);
        }

        var nextOwner = new Owner(string.IsNullOrWhiteSpace(accountId) ? ownerPrincipalId : accountId);
        var persistent = new BrainResidency.Persistent(ownerPrincipalId);
        var updated = new BrainStorage
        {
            Owner = nextOwner,
            CreatedAt = State.CreatedAt,
            Residency = persistent,
        };
        await Save(updated, new Claimed(nextOwner, ownerPrincipalId));
        residencyRegistry?.MarkPromoted(BrainId, ownerPrincipalId);
        return new BrainState(nextOwner, updated.CreatedAt) { Residency = persistent };
    }

    private async Task CreateCore(Owner owner, BrainResidency residency)
    {
        RequireCreator(owner);
        if (State.Owner is not null) { return; }
        var created = new Created(owner);
        await Save(new BrainStorage { Owner = owner, CreatedAt = DateTimeOffset.UtcNow, Residency = residency }, created);
        foreach (var handler in creation)
        {
            // One handler failing must not fail the brain; Activated handlers catch up later.
            try { await handler.Created(BrainId, created, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            { CreationHandlerFailed(logger, handler.GetType().Name, BrainId, error); }
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
        if (Context.Caller?.Actor is not Actor.Kernel) { throw new UnauthorizedAccessException("Only the kernel announces a brain's instances and types."); }
        return Publish(signal);
    }

    // Creating is the kernel's act, or the owner's own.
    private void RequireCreator(Owner owner)
    {
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("Only the platform or the owner may create a brain.");
        if (caller.Actor is Actor.Kernel || (caller.Actor is Actor.Person person && caller.Direct && person.Account == owner.AccountId)) { return; }
        throw new UnauthorizedAccessException("Only the platform or the owner may create a brain.");
    }

    // Claiming is the platform's act (after verifying session cookie + authenticated principal) or the direct claiming person.
    private void RequireClaimer(string ownerPrincipalId)
    {
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("Only the platform or the claiming principal may claim a brain.");
        if (caller.Actor is Actor.Kernel || (caller.Actor is Actor.Person person && caller.Direct && person.Account == ownerPrincipalId)) { return; }
        throw new UnauthorizedAccessException("Only the platform or the claiming principal may claim a brain.");
    }
}
