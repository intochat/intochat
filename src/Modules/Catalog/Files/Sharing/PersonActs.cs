using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Apps;

// Sharing is a person's act. The caller the kernel stamped must be a person (not an app, an agent, a module or a
// scheduler), acting directly or through a shipped screen of their own brain, in a brain their account owns.
internal static class PersonActs
{
    // Neuron types of shipped screens a person may act through. A unit or any other neuron is refused.
    public static readonly string[] Surfaces = ["app.friends"];

    public static async Task<(string Account, BrainId Brain)> Require(Caller? caller, IBrainOwners owners)
    {
        if (caller?.Actor is not Actor.Person person || string.IsNullOrEmpty(person.Account)) { throw Refused(); }
        // Via is the neuron whose proxy carried the person's call: none when they act directly or through the relay of
        // their own brain (a call made between brains carries no neuron of its own), otherwise it must be a listed screen.
        if (caller.Via is { } via && (via.Brain != caller.Brain || !Surfaces.Contains(via.Type, StringComparer.Ordinal))) { throw Refused(); }

        var owner = await owners.OwnerAccount(caller.Brain).ConfigureAwait(true);
        return string.Equals(owner, person.Account, StringComparison.Ordinal) ? (person.Account, caller.Brain) : throw Refused();
    }

    // The files module acting in its own brain: how a neuron reaches its brain's other neurons and storage while the
    // call it is serving was made by a person of another brain.
    public static IDisposable? AsFilesModule(IServiceProvider services, BrainId brain)
    {
        var edge = (EdgeAuthority?)services.GetService(typeof(EdgeAuthority));
        return edge?.Enter(edge.Module(FilesModule, brain));
    }

    public const string FilesModule = "apps.files";

    // One message for every refusal: it tells a caller nothing about which shares exist.
    public static UnauthorizedAccessException Refused() => new("Sharing is refused for this caller.");
}

internal sealed class IdentityBrainOwners(IServiceProvider services) : IBrainOwners
{
    public async Task<string?> OwnerAccount(BrainId brain, CancellationToken cancellationToken = default)
    {
        var broker = (ICapabilityBroker?)services.GetService(typeof(ICapabilityBroker));
        if (broker is null) { return null; }
        var identity = await broker.Identity(cancellationToken).ConfigureAwait(false);
        return (await identity.OwnerOfBrain(brain.Value, cancellationToken).ConfigureAwait(false))?.AccountId;
    }
}
