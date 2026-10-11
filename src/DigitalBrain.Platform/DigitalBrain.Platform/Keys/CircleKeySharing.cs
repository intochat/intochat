using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Keys;

// T26-5 on the machine that holds a circle's core: a contributed machine receives every existing part key (R127), and
// a removal rotates every part key to the machines that remain, then re-seals each part this machine can hold. A part
// another machine holds keeps its old key until its next open here or there, which re-seals it (PartLogs). A host with
// no part log holds no circle key and does nothing.
internal sealed partial class CircleKeySharing(CirclePartKeys keys, IKeyringClient keyring, ISequencerClient sequencer, IIdentityClient identity, ILogger<CircleKeySharing> logger,
    PartLogs? logs = null) : ICirclePartKeys
{
    public async Task ShareWith(string circleBrainId, string machineId, CancellationToken cancellationToken = default)
    {
        if (logs is null) { return; }
        foreach (var part in await keyring.CircleParts(circleBrainId))
        { await keys.WrapTo(PartId.Of(part), circleBrainId, machineId, cancellationToken); }
    }

    public async Task RevokeLeases(string circleBrainId, string machineId, CancellationToken cancellationToken = default)
    {
        if (logs is null) { return; }
        foreach (var part in await keyring.CircleParts(circleBrainId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sequencer.Revoke(circleBrainId, part, machineId);
        }
    }

    public async Task Rotate(string circleBrainId, CancellationToken cancellationToken = default)
    {
        if (logs is null) { return; }
        var machines = await identity.MachinesOf(circleBrainId);
        foreach (var part in (await keyring.CircleParts(circleBrainId)).Select(PartId.Of))
        {
            await keys.Rotate(part, circleBrainId, machines, cancellationToken);
            PartLog log;
            try { log = await logs.For(part); }
            catch (ServiceUnavailableException held)
            {
                LogHeldElsewhere(part, circleBrainId, held.Message);
                continue;
            }
            await log.Reseal(await keys.Unwrap(part, circleBrainId, cancellationToken));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Part {Part} of circle {Circle} is held by another machine; it is re-sealed under its rotated key on its next open ({Reason}).")]
    private partial void LogHeldElsewhere(PartId part, string circle, string reason);
}
