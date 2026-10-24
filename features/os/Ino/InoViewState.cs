using System.Text.Json;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;

// The sidebar's durable view state at {DataKey}/view-state, so a reconnect shows the same sidebar.
// Writes are compare-and-set on the text's version, so concurrent scripts never lose each other's change.
// It is also the view's one dirty signal: every write raises TextChanged, which view.cs, the single view writer,
// reacts to. The archive toast is stamped: once ToastLifetime has passed it is dropped and view.cs republishes.
public sealed record InoViewState(string Query = "", string? RenamingId = null, string? Toast = null, string? UndoId = null,
    DateTimeOffset? ToastAt = null, long Changes = 0)
{
    private const int Attempts = 8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IText Text(IntoChatOSConnection brain) => brain.Get<IText>(Ino.DataKey(brain) + "/view-state");

    public static async Task<InoViewState> Read(IText text, DateTimeOffset now) => Parse((await text.Read()).Markdown).Current(now);

    public static async Task<InoViewState> Update(IText text, Func<InoViewState, InoViewState> change, DateTimeOffset now)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var read = await text.Read();
            var changed = change(Parse(read.Markdown).Current(now));
            var next = changed with { Changes = changed.Changes + 1 };
            if (await text.TrySet(JsonSerializer.Serialize(next, Json), read.Version)) { return next; }
        }
        throw new InvalidOperationException("The Ino view state kept changing; try again.");
    }

    // Every write bumps Changes, so this one always raises TextChanged and view.cs republishes.
    public static Task<InoViewState> MarkDirty(IText text, DateTimeOffset now) => Update(text, state => state, now);

    private static InoViewState Parse(string markdown)
    {
        try { return markdown.Length == 0 ? new() : JsonSerializer.Deserialize<InoViewState>(markdown, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public DateTimeOffset? ToastUntil => ToastAt + InoLayout.ToastLifetime;

    public InoViewState Current(DateTimeOffset now) => ToastAt is { } at && now - at <= InoLayout.ToastLifetime ? this : Untoasted();

    public InoViewState Toasted(string text, string undoId, DateTimeOffset now) => this with { Toast = text, UndoId = undoId, ToastAt = now };

    public InoViewState Untoasted() => this with { Toast = null, UndoId = null, ToastAt = null };
}
