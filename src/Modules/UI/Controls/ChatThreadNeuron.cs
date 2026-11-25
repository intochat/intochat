/*
# Chat thread

A control neuron: the messages of a conversation. It owns the messages (the last 500); its parent writes them with AppendMessage,
UpdateMessage and ImportMessages (idempotent by id: a held id is replaced in place, a new one appended, null entries, blank ids and
repeated ids skipped). Every failed or interrupted assistant message draws its reason ("The model did not answer." when none is
given) and a retry; Retry(id) only signals RetryRequested, and the parent does the work. The composer's text stays on the client until
it is submitted; SubmitText signals Submitted with the text under "text", and Stop signals StopRequested. The placeholder, model,
status, empty text and an error no message carries are props its parent writes on the slot. Named ChatThread because a `conversation`
data node already exists; it draws as a chatThreadView with the conversation's renderer.
*/
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A chat thread: it owns the messages and signals what the user asks of them.</summary>
[Neuron("control.chatThread"), Emits(typeof(MessageAppended)), Emits(typeof(MessageUpdated)), Emits(typeof(RetryRequested)), Emits(typeof(Submitted)), Emits(typeof(StopRequested)), Emits(typeof(ChatModelSelected))]
public sealed partial class ChatThreadNeuron : ControlNeuron<ChatThreadState, ChatThreadProps>, IChatThreadControl
{
    [JsonSerializable(typeof(Dictionary<string, bool>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    private sealed partial class ChatThreadArgsJson : JsonSerializerContext;

    private static readonly JsonElement PinModelArgs = JsonSerializer.SerializeToElement(
        new Dictionary<string, bool> { ["default"] = true },
        ChatThreadArgsJson.Default.DictionaryStringBoolean);

    [On]
    public Task AppendMessage(AppendMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = command.Message ?? throw new ArgumentException("A message is required.", nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Id);
        if (State.Messages.Any(held => held.Id == message.Id)) { return Task.CompletedTask; }
        return Show(State with { Messages = [.. State.Messages.Append(Clean(message)).TakeLast(ChatThreadState.MaxMessages)] }, new MessageAppended(message.Id));
    }

    [On]
    public Task UpdateMessage(UpdateMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        ArgumentNullException.ThrowIfNull(command.Patch);
        var index = IndexOf(State.Messages, command.Id);
        if (index < 0) { return Task.CompletedTask; }
        var updated = Clean(command.Patch.ApplyTo(State.Messages[index]));
        if (updated.Equals(State.Messages[index])) { return Task.CompletedTask; }
        var messages = State.Messages.ToArray();
        messages[index] = updated;
        return Show(State with { Messages = messages }, new MessageUpdated(command.Id));
    }

    private static (bool Stop, bool Retry) Shows(ChatThreadProps? props) => (props is { Working: true }, props is { Error.Retry: true });

    [On]
    public Task ImportMessages(ImportMessages command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var next = Merge(command.Replace ? [] : State.Messages, command.Messages ?? []);
        // The stop and the error's retry show what the props routed with the parent's last write say (kept, so a redraw without
        // props still shows them); a write without props (not from the parent) leaves them.
        var (stop, retry) = Props is { } routed ? Shows(routed) : (State.ShowsStop, State.ShowsRetry);
        var accepts = Props is { } given ? given.CanSend && given.IsEnabled : State.AcceptsText;
        var updated = State with { Messages = next, ShowsStop = stop, ShowsRetry = retry, AcceptsText = accepts };
        return updated.Equals(State) ? Task.CompletedTask : Show(updated);
    }

    [On]
    public Task Retry(Retry command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = command.Id ?? string.Empty;
        if (id.Length > 0 && IndexOf(State.Messages, id) < 0) { throw new ArgumentException($"The thread holds no message '{id}'.", nameof(command)); }
        return Publish(new RetryRequested(id));
    }

    [On]
    public Task SubmitText(SubmitText command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // The parent said it cannot take a message (a reply is running): refused here, in order, before it could reach the parent.
        if (!State.AcceptsText) { throw new DigitalBrain.Units.ActivationRefusedException(ControlProps.Disabled); }
        return string.IsNullOrWhiteSpace(command.Value)
            ? Task.CompletedTask
            : Publish(new Submitted(new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = command.Value }));
    }

    [On]
    public Task Stop(Stop command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Publish(new StopRequested());
    }

    [On]
    public Task SelectModel(SelectModel command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Value)) { return Task.CompletedTask; }
        return Publish(new ChatModelSelected(Id.FullKey(), command.Value.Trim(), command.Default));
    }

    /// <summary>
    /// <paramref name="held"/> with <paramref name="incoming"/> merged: a held id is replaced in place, a new one appended; null entries,
    /// blank ids and an id repeated in <paramref name="incoming"/> (after its first) are skipped. The last 500 are kept.
    /// </summary>
    public static IReadOnlyList<ThreadMessage> Merge(IReadOnlyList<ThreadMessage> held, IReadOnlyList<ThreadMessage?> incoming)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(incoming);
        var result = held.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in incoming)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Id) || !seen.Add(message.Id)) { continue; }
            var clean = Clean(message);
            var index = result.FindIndex(m => m.Id == message.Id);
            if (index >= 0) { result[index] = clean; } else { result.Add(clean); }
        }
        return [.. result.TakeLast(ChatThreadState.MaxMessages)];
    }

    /// <summary>What a thread with <paramref name="messages"/> draws under <paramref name="key"/>: the last 100 messages (at most 1,000,000 characters of text, <see cref="ChatThreadProps.MaxDrawnMessages"/> and <see cref="ChatThreadProps.MaxDrawnChars"/>), each failed one with its reason and a retry.</summary>
    public static ChatThreadView Draw(string key, IReadOnlyList<ThreadMessage> messages, bool showsStop = false, bool showsRetry = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(messages);
        return new ChatThreadView(
            key,
            [.. Shown(messages).Select(DrawMessage)],
            new ComposerNode(ChatThreadProps.ComposerKey, new Emit("control.chatThread", "control.submitText")),
            showsStop ? new Button(ChatThreadProps.StopKey, "Stop", new Command(".", "control.stop"), AutomationName: "Stop") : null,
            showsRetry ? new Button(ChatThreadProps.ErrorRetryKey, "Try again", RetryCommand(string.Empty), AutomationName: "Try again") : null,
            SelectModel: new ModelSelectNode(ChatThreadProps.SelectModelKey, new Emit("control.chatThread", "control.selectModel")),
            PinModel: new ModelSelectNode(ChatThreadProps.PinModelKey, new Emit("control.chatThread", "control.selectModel", PinModelArgs)));
    }

    protected override View Render(ChatThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, Draw(Key, state.Messages, state.ShowsStop, state.ShowsRetry));
    }

    private static MessageNode DrawMessage(ThreadMessage message)
    {
        var failed = message.IsFailed && message.Role == "assistant";
        var canRegenerate = !failed && message.CanRegenerate && message.Role == "assistant" && message.Status == "completed";
        var retry = failed
            ? new Button(ChatThreadProps.RetryKeyOf(message.Id), "Try again", RetryCommand(message.Id), AutomationName: "Try again")
            : canRegenerate
                ? new Button(ChatThreadProps.RegenerateKeyOf(message.Id), "Regenerate", RetryCommand(message.Id), AutomationName: "Regenerate")
                : null;
        return new MessageNode(
            message.Id,
            message.Role,
            message.Text,
            Cards: message.Cards.Count > 0 ? message.Cards : null,
            Activities: message.Activities.Count > 0 ? message.Activities : null,
            Status: message.Status,
            Model: message.Model,
            Retry: retry,
            ModelLabel: message.Model,
            ProviderIcon: message.ProviderIcon,
            Error: failed ? new ErrorLineNode(string.IsNullOrWhiteSpace(message.Failure) ? ChatThreadProps.NoAnswer : message.Failure) : null);
    }

    private static Command RetryCommand(string id)
        => new(".", "control.retry", JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["id"] = id },
            ChatThreadArgsJson.Default.DictionaryStringString));

    private static IReadOnlyList<ThreadMessage> Shown(IReadOnlyList<ThreadMessage> messages)
    {
        var selected = new List<ThreadMessage>(Math.Min(messages.Count, ChatThreadProps.MaxDrawnMessages));
        var total = 0;
        for (var i = messages.Count - 1; i >= 0 && selected.Count < ChatThreadProps.MaxDrawnMessages; i--)
        {
            var text = messages[i].Text ?? string.Empty;
            if (selected.Count == 0 && text.Length > ChatThreadProps.MaxDrawnChars)
            {
                selected.Add(messages[i] with { Text = text[..ChatThreadProps.MaxDrawnChars] });
                break;
            }
            if (total + text.Length > ChatThreadProps.MaxDrawnChars) { break; }
            selected.Add(messages[i]);
            total += text.Length;
        }
        selected.Reverse();
        return selected;
    }

    // Null lists from the wire become empty ones, a null text an empty one.
    private static ThreadMessage Clean(ThreadMessage message) => message with
    {
        Text = message.Text ?? string.Empty,
        Role = string.IsNullOrWhiteSpace(message.Role) ? "assistant" : message.Role,
        Status = string.IsNullOrWhiteSpace(message.Status) ? "completed" : message.Status,
        Activities = message.Activities ?? [],
        Cards = message.Cards ?? [],
    };

    private static int IndexOf(IReadOnlyList<ThreadMessage> messages, string id)
    {
        for (var i = 0; i < messages.Count; i++) { if (messages[i].Id == id) { return i; } }
        return -1;
    }
}
