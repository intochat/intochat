namespace DigitalBrain.Platform.Storage;

public sealed class BrainStoreOptions
{
    public const string Key = DigitalBrain.Framework.DigitalBrainNames.BrainStorageConfigurationKey;
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "runtime");
    public DigitalBrain.Framework.Parts.MasterKeyCustodyKind Custody { get; set; }
    public bool ConfiguredCustodyAcknowledged { get; set; }
    public BrainLogFormat WriteFormat { get; set; } = BrainLogFormat.Legacy;
}
