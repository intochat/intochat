namespace DigitalBrain.Platform.Storage;

public sealed class BrainStoreOptions
{
    public const string Key = "DigitalBrain:Platform:Storage";
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "runtime");
}
