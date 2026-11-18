using System.Text.Json;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Tests;

// What a person sees of a chat thread slot: its chatThreadView composed with the slot's props the way the client composes them
// (ui/lib/src/nodes/chat_thread_view.dart): the thread's messages, composer, stop and retry; the props' placeholder, model, status,
// empty text and error.
public static class ChatThreadComposition
{
    public static Conversation Compose(ChatThreadView view, ChatThreadProps props)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(props);
        var error = props.Error is { } e
            ? new ErrorLineNode(e.What, e.Detail, e.ActionLabel is null ? null
                : e.Retry ? view.Retry is { } retry ? retry with { Label = e.ActionLabel } : null
                : e.Open is { } open ? new Button("error-open", e.ActionLabel, Intent: new Intent(IntentKind.OpenWindow, open))
                : null)
            : null;
        return new Conversation(
            view.Key,
            view.Messages,
            view.Composer with
            {
                Placeholder = props.Placeholder,
                Enabled = props.CanSend && props.IsEnabled,
                ActiveModelLabel = props.ActiveModelLabel,
                ActiveProviderIcon = props.ActiveProviderIcon,
                Dictation = props.Dictation,
            },
            Status: props.Status,
            Error: error,
            Stop: props.Working ? view.Stop : null,
            Empty: view.Messages.Count == 0 && props.EmptyText is { } empty ? new EmptyStateNode(empty) : null);
    }

    // The neuron uri and props of the first slot in a view's wire JSON; null when it has none.
    public static (string NeuronUri, JsonElement Props)? FirstSlot(JsonElement view)
    {
        var pending = new Stack<JsonElement>([view]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("refKind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "slot"
                    && element.TryGetProperty("neuronUri", out var uri) && element.TryGetProperty("props", out var props))
                {
                    return (uri.GetString()!, props);
                }
                foreach (var property in element.EnumerateObject()) { pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { pending.Push(item); }
            }
        }
        return null;
    }

    public static ChatThreadProps PropsOf(JsonElement props) => props.Deserialize<ChatThreadProps>(JsonSerializerOptions.Web)!;
}
