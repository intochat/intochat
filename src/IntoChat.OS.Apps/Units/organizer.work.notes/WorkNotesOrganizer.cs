using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.WorkNotes;

[Signal("notes.indexed")]
public sealed record NotesIndexed(int NoteCount) : Signal;

[State("organizer.work.notes")]
public sealed record OrganizerState(int NoteCount = 0, string LatestNote = "")
{
    public OrganizerState() : this(0, "") { }
}

public static class WorkNotesOrganizerUnit
{
    [Synapse(typeof(Tick), "organizer.work.notes", Every = "every 15m")]
    public static Reaction<OrganizerState> Poll(Tick tick, OrganizerState state)
        => Reaction<OrganizerState>.Of(state, new NotesIndexed(state.NoteCount));

    [Synapse("notes.index", "organizer.work.notes")]
    public static Reaction<OrganizerState> Index(SignalEnvelope env, OrganizerState state)
    {
        var latest = Read(env.Payload, "latest") ?? state.LatestNote;
        var count = state.NoteCount;
        if (Read(env.Payload, "notes") is { } raw && !string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            count = parts.Length;
            if (string.IsNullOrWhiteSpace(latest) && count > 0)
            {
                latest = parts[0];
            }
        }
        else if (env.Payload.ValueKind == JsonValueKind.Object && env.Payload.TryGetProperty("count", out var c) && c.TryGetInt32(out var cnt))
        {
            count = cnt;
        }

        return Reaction<OrganizerState>.Of(state with { NoteCount = count, LatestNote = latest }, new NotesIndexed(count));
    }

    [Synapse("notes.reset", "organizer.work.notes")]
    public static OrganizerState Reset(SignalEnvelope env, OrganizerState state) => state with { NoteCount = 0, LatestNote = "" };

    [Synapse("organizer.work.notes")]
    public static View Show(OrganizerState state) => new("Workspace Markdown Organizer", new Column(
    [
        new Heading("Workspace Markdown Organizer", Subtitle: string.IsNullOrEmpty(state.LatestNote) ? "No notes indexed yet." : $"Latest: {state.LatestNote}"),
        new Badge($"{state.NoteCount} markdown note(s)", Tone: state.NoteCount > 0 ? "accent" : "neutral"),
        new Button("reset-notes", "Reset Index", new Emit("organizer.work.notes", "notes.reset")),
    ]) { Gap = 8 });

    private static string? Read(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            { return property.Value.GetString(); }
        }
        return null;
    }
}
