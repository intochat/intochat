using System.Text.Json;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;

// The sidebar's durable view state at {DataKey}/view-state, so a reconnect shows the same sidebar.
// The archive toast is stamped: a compose after ToastLifetime drops it, so it never comes back stale.
public sealed record InoViewState(string Query = "", string? RenamingId = null, string? Toast = null, string? UndoId = null,
    DateTimeOffset? ToastAt = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IText Text(IntoChatOSConnection brain) => brain.Get<IText>(Ino.DataKey(brain) + "/view-state");

    public static async Task<InoViewState> Load(IntoChatOSConnection brain)
    {
        var markdown = (await Text(brain).Read()).Markdown;
        try { return markdown.Length == 0 ? new() : JsonSerializer.Deserialize<InoViewState>(markdown, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static Task Save(IntoChatOSConnection brain, InoViewState state)
        => Text(brain).Set(JsonSerializer.Serialize(state, Json));

    public InoViewState Current(DateTimeOffset now)
        => ToastAt is { } at && now - at <= InoLayout.ToastLifetime ? this : this with { Toast = null, UndoId = null, ToastAt = null };

    public InoViewState Toasted(string text, string undoId, DateTimeOffset now) => this with { Toast = text, UndoId = undoId, ToastAt = now };
}
