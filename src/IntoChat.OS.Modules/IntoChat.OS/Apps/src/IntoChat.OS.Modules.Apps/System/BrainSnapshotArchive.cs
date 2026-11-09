namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Canonical alias and facade over <see cref="PortableBrainArchive"/> providing brain snapshot export, import, and JSON serialization.
/// </summary>
public static class BrainSnapshotArchive
{
    public const string CurrentFormatVersion = PortableBrainArchive.CurrentFormatVersion;

    public static Task<PortableBrainSnapshot> ExportAsync(
        string brainId,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio)
        => PortableBrainArchive.ExportAsync(brainId, settings, profile, studio);

    public static string ToJson(PortableBrainSnapshot snapshot)
        => PortableBrainArchive.ToJson(snapshot);

    public static PortableBrainSnapshot FromJson(string json)
        => PortableBrainArchive.FromJson(json);

    public static Task ImportAsync(
        PortableBrainSnapshot snapshot,
        IUserSettings settings,
        IProfile profile,
        ISynapseStudio studio)
        => PortableBrainArchive.ImportAsync(snapshot, settings, profile, studio);
}
