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

// R125/T26-13: a circle brain is born with no OS default app. Its core is founded with the name it was created under
// and the machine it is born on, the creator's (R76), contributed first. Create awaits this handler as the creator
// (BrainBirths stamps the owner), so the brain is complete before anyone sees it. Any other brain is left alone.
// The circle room was a script app and went with the script lane; a compiled room is not shipped yet.
internal sealed class CircleBirth(ICapabilityBroker broker, IServiceProvider plain) : ICreationHandler
{
    public Task Created(string brainId, Created created, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        return Born(brainId, cancellationToken);
    }

    // Idempotent: the core is founded once, so a birth that failed part-way is completed by running it again
    // (the factory does, on a retried Create).
    public async Task Born(string brainId, CancellationToken cancellationToken)
    {
        var directory = await broker.Identity(cancellationToken);
        if (await directory.KindOf(brainId, cancellationToken) != BrainKind.Circle) { return; }
        if (!RuntimeCallerContext.TryGet(out var creator)) { throw new InvalidOperationException($"Circle {brainId} is born only for its creator."); }
        var name = (await directory.Membership(brainId, creator.PersonId, cancellationToken))?.DisplayName
            ?? throw new InvalidOperationException($"Circle {brainId} has no creator on record.");
        await SiloNeurons.Get<ICircle>(plain, ICircle.Key(brainId)).Found(name);
    }
}
