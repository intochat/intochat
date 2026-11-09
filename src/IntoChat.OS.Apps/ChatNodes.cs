using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter.Chat;

namespace IntoChat.OS.Apps;

// A chat on the one UI path: the transcript and its composer are view nodes the app renders, its storage the IChat neuron.
// DigitalBrain.UI has no dedicated ChatTranscript or Composer node yet, so the transcript is a column of cards and the composer
// a keyed field whose submit emits to the app (the field's text joins the emit's args as "value").
public static class ChatNodes
{
    public static Node Transcript(IReadOnlyList<ChatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new Column([.. entries.Select(Entry)]) { Gap = 8 };
    }

    public static Node Entry(ChatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var who = entry.Role == ChatRole.User ? "You" : entry.Model is { Length: > 0 } model ? $"Assistant · {model}" : "Assistant";
        var body = entry.Status == "failed" && entry.Error is { Length: > 0 } error ? $"{entry.Text}\n\n{error}" : entry.Text;
        return new Card(who, body, [.. entry.Activities.Select(activity => (Node)new Text($"{activity.Title}: {activity.Result ?? activity.Input}") { Tone = "muted" })]);
    }

    // The composer: the field neuron at fieldKey holds the draft; submitting emits onSend with the text.
    public static Node Composer(string key, string fieldKey, Emit onSend, string placeholder = "Message")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);
        ArgumentNullException.ThrowIfNull(onSend);
        return new Field(key, fieldKey, FieldKind.Text, placeholder, OnSubmit: onSend);
    }
}
