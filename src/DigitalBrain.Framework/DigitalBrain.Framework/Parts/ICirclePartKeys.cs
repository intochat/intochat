namespace DigitalBrain.Framework.Parts;

// T26-5/R127: a circle brain's part keys are wrapped to each machine contributed to it, never under one custody root.
// The circle core calls these on the machine that holds it, after the trust side recorded the change: a contributed
// machine receives every existing part key, and a removal rotates every part key to the machines that remain and
// re-seals what this machine holds under the new key. A host with no part log holds no circle key and does nothing.
public interface ICirclePartKeys
{
    Task ShareWith(string circleBrainId, string machineId, CancellationToken cancellationToken = default);

    Task Rotate(string circleBrainId, CancellationToken cancellationToken = default);
}
