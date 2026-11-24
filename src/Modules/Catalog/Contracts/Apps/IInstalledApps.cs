using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// A single launcher/dock tile representing a compiled app, a runtime-installed app,
/// or a deployed Synapse micro-app unit visible to a brain.
/// </summary>
[GenerateSerializer, Alias("apps.tile")]
public sealed record AppTile(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Icon,
    [property: Id(3)] string? Category,
    [property: Id(4)] int? Dock,
    [property: Id(5)] AppLaunch Launch,
    [property: Id(6)] string Kind,
    [property: Id(7)] string? Description = null);

/// <summary>
/// Single authoritative read-only catalog of all apps installed for a brain,
/// merging compiled screens/bundles, runtime <see cref="IApp"/> instances, and deployed <see cref="ISynapseStudio"/> units.
/// </summary>
[NeuronAlias("apps.installed"), DefaultNeuronType("apps.installed")]
public interface IInstalledApps
{
    const string DefaultKey = "main";

    [ReadOnlyCall]
    Task<AppTile[]> List();
}
