namespace DigitalBrain.Framework;

public static class DigitalBrainNames
{
    public const string GrainStateContainer = "digitalbrain-v2-state";
    public const string GrainStateContainerKey = "DigitalBrain:Orleans:GrainStateContainer";

    public const string Storage = "storage";
    public const string Clustering = "clustering";
    public const string Reminders = "reminders";
    public const string GrainState = "grainstate";
    public const string ClusterGrainStorage = "cluster";
    public const string PlatformGrainStorage = "platform";
    public const string MasterKeyConfigurationKey = "DigitalBrain:MasterKey";
    public const string MasterKeyEnvironmentVariable = "DigitalBrain__MasterKey";
    // PartsOptions.Custody's key, here so a per-OS module reads it without the Platform ring.
    public const string CustodyConfigurationKey = "DigitalBrain:Platform:Parts:Custody";
    // The link certificate extension holding a machine key's endorsement (T20-2, R85): an arc under 2.25 (ITU-T X.667,
    // no registration) from UUID f274567e-24e5-4ea4-8a71-ea207b00a3f3, cut to its high 64 bits because Windows
    // CryptoAPI refuses an OID component wider than 64 bits (a certificate carrying the full 128-bit arc cannot be made).
    public const string LinkEndorsementOid = "2.25.17470683954261872292";
}
