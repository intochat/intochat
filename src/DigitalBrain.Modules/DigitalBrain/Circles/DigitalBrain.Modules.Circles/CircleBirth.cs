using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Circles;

// R125/T26-13: a circle brain is born with exactly its own app, features/os/CircleRoom, and no OS default. Its core
// is founded with the name it was created under and the machine it is born on, the creator's (R76), contributed
// first. Create awaits this handler as the creator (BrainBirths stamps the owner), so the brain is complete before
// anyone sees it. Any other brain is left alone.
// The room loads through the Apps module; a host without it has no circle brains to give a room.
internal sealed class CircleBirth(IIdentityClient identity, IGrainFactory grains, FeatureCatalog? catalog = null, FeatureLoader? loader = null) : ICreationHandler
{
    public const string RoomOwner = "os";
    public const string RoomName = "circleroom";

    public async Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var brainId = kernel.GetPrimaryKeyString();
        if (await identity.KindOf(brainId) != BrainKind.Circle) { return; }
        if (catalog is null || loader is null) { throw new InvalidOperationException($"Circle {brainId} needs the Apps module to load its room."); }
        if (!CallerContextStamper.TryGet(out var creator)) { throw new InvalidOperationException($"Circle {brainId} is born only for its creator."); }
        var name = (await identity.Membership(brainId, creator.PrincipalId))?.DisplayName
            ?? throw new InvalidOperationException($"Circle {brainId} has no creator on record.");
        var machine = identity.MachineId ?? throw new InvalidOperationException("A circle is born only on a machine.");
        await identity.ContributeMachine(brainId, machine);
        await grains.GetGrain<ICircle>(ICircle.Key(brainId)).Found(name, machine);
        var room = await catalog.Find(RoomOwner, RoomName)
            ?? throw new InvalidOperationException($"The features folder does not carry {RoomOwner}/{RoomName}; circle {brainId} has no room.");
        await loader.Load(brainId, RoomName, room, new AppOrigin(RoomOwner, new AppSource.Shipped(), RoomName, room.Version), cancellationToken);
    }
}
