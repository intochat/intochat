using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Signals;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Circles;

// Legacy circle brain creation handler: founds the circle record if a BrainKind.Circle is created.
// Personal brains now connect directly via FriendsApp and ProfileNeuron without requiring separate CircleRoom apps.
internal sealed class CircleBirth(ICapabilityBroker broker, IServiceProvider plain, FeatureCatalog? catalog = null, FeatureLoader? loader = null) : ICreationHandler
{
    public const string RoomOwner = "os";
    public const string RoomName = "friends";

    public Task Created(string brainId, Created created, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return Born(brainId, cancellationToken);
    }

    public async Task Born(string brainId, CancellationToken cancellationToken)
    {
        var directory = await broker.Identity(cancellationToken);
        if (await directory.KindOf(brainId, cancellationToken) != BrainKind.Circle) { return; }
        if (!RuntimeCallerContext.TryGet(out var creator)) { throw new InvalidOperationException($"Circle {brainId} is born only for its creator."); }
        var name = (await directory.Membership(brainId, creator.PersonId, cancellationToken))?.DisplayName
            ?? throw new InvalidOperationException($"Circle {brainId} has no creator on record.");
        await SiloNeurons.Get<ICircle>(plain, ICircle.Key(brainId)).Found(name);
        if (catalog is not null && loader is not null && await catalog.Find(RoomOwner, RoomName) is { } room)
        {
            await loader.Load(brainId, RoomName, room, new AppOrigin(RoomOwner, new AppSource.Shipped(), RoomName, room.Version), cancellationToken);
        }
    }
}
