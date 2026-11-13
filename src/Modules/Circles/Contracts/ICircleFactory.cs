
namespace IntoChat.OS.Modules.Circles;

// A circle brain the member's index lists: its id, and its name where this brain created it (a joined circle's name
// is read inside the circle brain, through the member's own route).
public sealed record CircleEntry(string BrainId, string Name);

// R126: in the member's own brain, `{brain}/circles`; the only script-reachable way to create a circle. Accepting and
// inviting happen inside the circle brain, on the member's human route.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("circles.factory"), DigitalBrain.Contracts.DefaultNeuronType("circles.factory")]
public interface ICircleFactory
{
    // CreateBrain(Kind = Circle) for the caller's account, then the circle brain's birth; returns the circle brain id.
    // A requestId makes it idempotent: creating twice under one id is one circle.
    Task<string> Create(string name, string? requestId = null);

    // The caller's circle brains, from the trust side's BrainsOf.
    Task<CircleEntry[]> Circles();
    Task<CirclePublicationFeed> Publications(string? publisher = null);
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<IntoChat.OS.Modules.Apps.AppSnapshot> Fork(IntoChat.OS.Modules.Apps.AppPublicationRef publication, string targetName, Guid operationId);

    static string Key(string brainId) => $"{brainId}/circles";
}
