using DigitalBrain.Capabilities;
using DigitalBrain.Modules;

namespace DigitalBrain.Identity.Local.Directory;

/// <summary>
/// DigitalBrain module marker for <see cref="DirectoryNeuron"/> and <see cref="SessionsNeuron"/>.
/// Lives in the disjoint <c>DigitalBrain.Identity.Local.Directory</c> namespace so hosts can compose
/// the vault module without pulling in the local directory neuron when another directory is hosted.
/// </summary>
[Module(Id)]
public sealed class LocalDirectoryModule : Module
{
    /// <summary>Module identifier.</summary>
    public const string Id = "local-directory";

    /// <summary>Module manifest.</summary>
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.State("directory")],
        Settings: [],
        Secrets: []);
}
