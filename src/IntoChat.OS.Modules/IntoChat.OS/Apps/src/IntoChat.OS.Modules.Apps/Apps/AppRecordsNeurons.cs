namespace IntoChat.OS.Modules.Apps;

// The store behind the Desktop's records: what the app writes is what it reads after any activation.
[DigitalBrain.Neuron("apps.desktop-records")]
public sealed class DesktopRecordsNeuron : DigitalBrain.Neuron<DesktopRecords>, IDesktopRecords
{
    public Task<DesktopRecords> Read() => Task.FromResult(State);

    public async Task Write(DesktopRecords records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(records.Widgets);
        AppRecordsOwner.Require(Id.FullKey());
        // A desktop keeps at most 64 widgets, the first opened.
        await Save(records with { Widgets = [.. records.Widgets.Take(64)], Layout = records.Layout ?? "" }, new DesktopRecordsChanged());
    }
}

// The store behind Ino's records.
[DigitalBrain.Neuron("apps.ino-records")]
public sealed class InoRecordsNeuron : DigitalBrain.Neuron<InoRecords>, IInoRecords
{
    public Task<InoRecords> Read() => Task.FromResult(State);

    public async Task Write(InoRecords records)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(records.Links);
        AppRecordsOwner.Require(Id.FullKey());
        // Ino keeps the newest links only.
        await Save(records with { Links = [.. records.Links.TakeLast(IntoChat.OS.Modules.Flutter.Chat.ChatAppCard.MaxLinksKept)] }, new InoRecordsChanged());
    }
}

// Only the owning app ({brain}/apps/{app}/…), its brain's person or its brain's platform writes; every other caller kind, another
// brain and a call with no stamped caller are refused.
internal static class AppRecordsOwner
{
    public static void Require(string key)
    {
        if (!DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.TryGet(out var caller)) { throw Refused(); }
        if (!DigitalBrain.Brains.BrainKey.BelongsTo(key, caller.BrainId)) { throw Refused(); }
        var allowed = caller.Kind switch
        {
            DigitalBrain.Capabilities.CallerKind.User or DigitalBrain.Capabilities.CallerKind.Platform => true,
            DigitalBrain.Capabilities.CallerKind.App => !string.IsNullOrEmpty(caller.AppId) && key.StartsWith(caller.AppId + "/", System.StringComparison.Ordinal),
            _ => false,
        };
        if (!allowed) { throw Refused(); }
    }

    private static System.UnauthorizedAccessException Refused() => new("Only the owning brain and app may write these records.");
}
