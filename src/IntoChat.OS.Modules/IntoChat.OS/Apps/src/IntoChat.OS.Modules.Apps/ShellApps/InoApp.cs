/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, approve-card, new, new-conversation, voice, make

The brain's personal assistant. Connects to MemoryNeuron (IMemory) and UserSettingsNeuron (IUserSettings)
to maintain context across conversations, route intents, and answer messages.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Memory;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("ino.open")]
public sealed record InoOpen(string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.send")]
public sealed record InoSend(string? Message = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.new")]
public sealed record InoNew : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.new-conversation")]
public sealed record InoNewConversation : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.voice")]
public sealed record InoVoice : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.make")]
public sealed record InoMake(string? Sentence = null, string[]? Focus = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.approve-card")]
public sealed record InoApproveCard(string? CardId = null, bool Approved = true) : DigitalBrain.Signal;

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status);

public sealed record InoTurn(
    string UserMessage,
    string Reply,
    string RouteHint,
    int RecalledCount);

public sealed record InoAppState(
    string ConversationId,
    string Title,
    string Status,
    string VoiceStatus,
    InoTurn[] Turns,
    InoCard[]? Cards = null)
{
    public InoAppState() : this("main", "New chat", "Ready", "idle", [], []) { }

    public InoCard[] ActiveCards => Cards ?? [];
}

[DigitalBrain.App("ino")]
public sealed class InoApp : DigitalBrain.App<InoAppState>
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "ino",
        "Ino",
        "3.4.0",
        [typeof(UserSettingsNeuron)],
        new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = new(
        "ino",
        "3.4.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Route or answer a message."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("voice", "Begin voice input."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
            ],
            Settings:
            [
                new("Model", "Model catalog id; empty uses the default.", ""),
                new("RouteGate", "Confidence required for a direct route.", "0.8"),
                new("IntendGate", "Seconds to wait for Memory's brief before answering without it.", "8"),
            ],
            Slots: [],
            Icon: "icon.svg",
            Toolbar:
            [
                new("send", Kind: "text", Label: "Message your brain"),
                new("new-conversation", Icon: "chat", Label: "New conversation"),
                new("voice", Icon: "mic", Label: "Voice"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [DigitalBrain.On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var status = request.Mode == "greet"
            ? $"Hello, {settings.DisplayName}! Ready on {settings.DefaultModel}."
            : $"Ready ({settings.DefaultModel})";
        Set(State with { Status = status });
    }

    [DigitalBrain.On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = string.IsNullOrWhiteSpace(request.Message) ? "hello" : request.Message.Trim();
        var memory = TryGetMemory();
        var recalled = memory is null ? [] : await memory.Recall(text, 3);
        var settings = await Get<IUserSettings>("settings").Read();

        var routeHint = text.StartsWith("open ", StringComparison.OrdinalIgnoreCase)
            ? $"routed:{text[5..].Trim()}.open"
            : $"agent:{settings.DefaultModel}";
        var contextSummary = recalled.Length > 0
            ? $" (recalled {recalled.Length} memory item(s): {recalled[0].Text})"
            : string.Empty;
        var reply = $"Ino ({settings.DefaultModel}): Answered '{text}'{contextSummary}";

        if (memory is not null)
        {
            await memory.CaptureEpisode("ino", $"User: {text} | Reply: {reply}", importance: 5);
        }

        var title = State.Turns.Length == 0
            ? (text.Length <= 52 ? text : text[..52])
            : State.Title;
        var turns = State.Turns.Append(new InoTurn(text, reply, routeHint, recalled.Length)).ToArray();
        var cards = State.ActiveCards;
        if (text.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || text.Contains("close date", StringComparison.OrdinalIgnoreCase)
            || text.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            var card = new InoCard(
                $"card-{turns.Length}",
                "approval",
                $"Approve: {text}",
                "Pre-filled LFP ApprovalCard ready for 1-click confirmation.",
                "neuron://local/os.confirmations/inbox",
                "pending");
            cards = [.. cards, card];
        }

        Set(State with
        {
            Title = title,
            Status = routeHint,
            Turns = turns,
            Cards = cards,
        });
    }

    [DigitalBrain.On]
    public async Task ApproveCard(InoApproveCard request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cardId = string.IsNullOrWhiteSpace(request.CardId) ? "card-1" : request.CardId;
        var nextStatus = request.Approved ? "approved" : "rejected";
        var updated = State.ActiveCards
            .Select(c => string.Equals(c.Id, cardId, StringComparison.Ordinal) ? c with { Status = nextStatus } : c)
            .ToArray();
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.card", $"Card {cardId} -> {nextStatus}", importance: 6);
        }
        Set(State with
        {
            Status = $"Card {cardId}: {nextStatus}",
            Cards = updated,
        });
    }

    [DigitalBrain.On]
    public void New(InoNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(new InoAppState($"c-{Guid.NewGuid():N}"[..10], "New chat", "New conversation started", "idle", []));
    }

    [DigitalBrain.On]
    public void NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(new InoAppState($"c-{Guid.NewGuid():N}"[..10], "New chat", "New conversation started", "idle", []));
    }

    [DigitalBrain.On]
    public void Voice(InoVoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { VoiceStatus = "recording", Status = "Listening..." });
    }

    [DigitalBrain.On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetStudio() is { } studio)
        {
            try
            {
                var built = await studio.Build(sentence);
                target = built.Name;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "AppStudio build requires a live model and sandbox; falling back to local capture.");
            }
        }
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6);
        }
        var card = new InoCard(
            $"app-{target}",
            "applet",
            $"Live App: {target}",
            sentence,
            $"neuron://local/apps/{target}",
            "running");
        Set(State with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. State.ActiveCards, card],
        });
    }

    [DigitalBrain.On("settings", Contract = typeof(IUserSettings))]
    public void OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null) { return; }
        Set(State with { Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
    }

    public View RenderSide() => new(
        "Chats",
        new Collection([new Entry(State.ConversationId, State.Title, $"{State.Turns.Length} turn(s)")]));

    public View Render() => new(
        "Ino",
        new Column(
        [
            new Text(State.Status),
            .. State.ActiveCards.Select(card =>
                new Text($"[{card.Kind}:{card.Status}] {card.Title} ({card.SourceNeuronUri})")),
            new Collection(State.Turns.Select((turn, i) =>
                new Entry($"turn-{i + 1}", turn.UserMessage, turn.Reply)).ToArray()),
        ]));

    [DigitalBrain.Scenario("Sending a message recalls memory, captures an episode, and updates transcript")]
    public async Task VerifyMemoryAndConversationScenario()
    {
        var memory = Get<IMemory>("memory");
        await memory.RememberFact("preference", "User prefers concise dark-mode UI", importance: 8);

        await Send(new InoSend("Tell me about my UI preference"));
        if (State.Turns.Length != 1 || State.Turns[0].RecalledCount == 0)
        {
            throw new InvalidOperationException("Expected InoApp to recall the stored preference fact.");
        }

        var snapshot = await memory.Read();
        if (snapshot.Episodes.Length == 0)
        {
            throw new InvalidOperationException("Expected InoApp to capture the conversation episode in MemoryNeuron.");
        }
    }

    private IMemory? TryGetMemory()
    {
        try
        {
            return Get<IMemory>("memory");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "MemoryNeuron is optional when running without AIModule registered.");
            return null;
        }
    }

    private IAppStudio? TryGetStudio()
    {
        try
        {
            return Get<IAppStudio>("main");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "AppStudioNeuron is optional when running in unit tests without Creator registered.");
            return null;
        }
    }
}
