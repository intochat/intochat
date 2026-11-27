using DigitalBrain.Capabilities;
using DigitalBrain.Modules;

namespace DigitalBrain.Identity.Local.Vault;

/// <summary>
/// DigitalBrain module marker for <see cref="VaultNeuron"/> (<c>identity.vault</c>).
/// Lives in the disjoint <c>DigitalBrain.Identity.Local.Vault</c> namespace so hosts can compose
/// <see cref="VaultNeuron"/> independently of <c>DigitalBrain.Identity.Local.Directory</c>.
/// </summary>
[Module(Id)]
public sealed class LocalVaultModule : Module
{
    /// <summary>Module identifier.</summary>
    public const string Id = "local-vault";

    /// <summary>Module manifest.</summary>
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.State("vault")],
        Settings: [],
        Secrets: []);
}
