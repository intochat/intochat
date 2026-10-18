namespace DigitalBrain.Authority.Storage;

public sealed class BrainStoreOptions
{
    public const string Key = DigitalBrain.Framework.DigitalBrainNames.BrainStorageConfigurationKey;
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "runtime");
    public DigitalBrain.Framework.Parts.MasterKeyCustodyKind Custody { get; set; }
    public bool ConfiguredCustodyAcknowledged { get; set; }
    // Shipped OS config writes Legacy until the release after v0.1.0; flip by setting WriteFormat=Framed.
    public BrainLogFormat WriteFormat { get; set; } = BrainLogFormat.Legacy;
}
