namespace DigitalBrain.Aspire.Hosting;

public static class DigitalBrainHostingNames
{
    public const string Kernel = "Kernel";
    public const string Orleans = "digitalbrain";
    public const string MasterKeyParameter = "digitalbrain-master-key";

    // Azurite lifetime. Product runs persist; a test host passes false so deployments do not share a volume.
    public const string PersistentStorageKey = "DigitalBrain:Hosting:PersistentStorage";
}
