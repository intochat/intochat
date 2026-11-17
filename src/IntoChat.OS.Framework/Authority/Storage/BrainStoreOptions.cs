using DigitalBrain.Identity.Local.Custody;
namespace IntoChat.OS.Authority.Storage;

// The os side of the brain store (06-02): where the runtime BrainLog lives and the master key custody checks. The runtime
// owns the log, its format and its writer ownership (the cloud profile's blob lease); Ownership:* keys are no longer read here.
public sealed class OsBrainStoreOptions
{
    public const string Key = IntoChat.OS.Contracts.Configuration.IntoChatOSNames.BrainStorageConfigurationKey;
    // <Home>/state: the hosting registrations set it from IntoChatOS:Home (StateHome); it is not a configuration key.
    public string Directory { get; set; } = DigitalBrain.Runtime.Orleans.StateHome.Default().State;
    public DigitalBrain.Identity.Local.Custody.MasterKeyCustodyKind Custody { get; set; }
    public bool ConfiguredCustodyAcknowledged { get; set; }
}
