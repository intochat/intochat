using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Circles;

// R125/T26-13: a circle brain is born with exactly its own app, features/os/CircleRoom, and no OS default. Its core
// is founded with the name it was created under and the machine it is born on, the creator's (R76), contributed
// first. Create awaits this handler as the creator (BrainBirths stamps the owner), so the brain is complete before
// anyone sees it. Any other brain is left alone.
// The room loads through the Apps module; a host without it has no circle brains to give a room.
internal sealed class CircleBirth(IIdentityClient identity, IServiceProvider plain, FeatureCatalog? catalog = null, FeatureLoader? loader = null) : ICreationHandler
{
    public const string RoomOwner = "os";
    public const string RoomName = "circleroom";

    public Task Created(string brainId, Created created, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return Born(brainId, cancellationToken);
    }

    // Idempotent: the core is founded once and the room loaded again in place, so a birth that failed part-way is
    // completed by running it again (the factory does, on a retried Create).
    public async Task Born(string brainId, CancellationToken cancellationToken)
    {
        if (await identity.KindOf(brainId) != BrainKind.Circle) { return; }
        if (catalog is null || loader is null) { throw new InvalidOperationException($"Circle {brainId} needs the Apps module to load its room."); }
        if (!CallerContextStamper.TryGet(out var creator)) { throw new InvalidOperationException($"Circle {brainId} is born only for its creator."); }
        var name = (await identity.Membership(brainId, creator.PersonId))?.DisplayName
            ?? throw new InvalidOperationException($"Circle {brainId} has no creator on record.");
        await SiloNeurons.Get<ICircle>(plain, ICircle.Key(brainId)).Found(name);
        var room = await catalog.Find(RoomOwner, RoomName)
            ?? throw new InvalidOperationException($"The features folder does not carry {RoomOwner}/{RoomName}; circle {brainId} has no room.");
        await loader.Load(brainId, RoomName, room, new AppOrigin(RoomOwner, new AppSource.Shipped(), RoomName, room.Version), cancellationToken);
    }
}
