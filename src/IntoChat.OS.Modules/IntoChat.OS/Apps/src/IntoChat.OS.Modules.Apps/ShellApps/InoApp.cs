/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, approve-card, connect-db, query-db, new, new-conversation, voice, make

The brain's personal assistant. Connects to MemoryNeuron (IMemory), UserSettingsNeuron (IUserSettings),
Files VFS (IFilesVfsService), DecisionMaker (IDecisionMaker), and Postgres connections to maintain context
across conversations, route intents, inspect/write workspace files, query databases, and render LFP cards.
*/
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("ino.open")]
public sealed record InoOpen(string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null) : DigitalBrain.Signal
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : Text;
}

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

[DigitalBrain.Signal("ino.connect-db")]
public sealed record InoConnectDb(string? ConnectionString = null, string? Alias = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.query-db")]
public sealed record InoQueryDb(string? Sql = null) : DigitalBrain.Signal;

public sealed record InoChartPoint(
    string Id,
    string Label,
    double Value);

public sealed record InoCard(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string SourceNeuronUri,
    string Status,
    string? SurfaceScopeUri = null,
    string[]? Columns = null,
    string[][]? Rows = null,
    string? ChartKind = null,
    InoChartPoint[]? ChartPoints = null)
{
    public string EffectiveScopeUri => SurfaceScopeUri ?? $"neuron://local/apps/ino#{Id}";
    public bool IsWaiting => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
        && Kind is "approval" or "choice" or "draft" or "secret" or "app-install";
}

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
    InoCard[]? Cards = null,
    string? ConnectedDatabase = null,
    bool IsPinned = true)
{
    public InoAppState() : this("main", "New chat", "Ready", "idle", [], [], null, true) { }

    public InoCard[] ActiveCards => Cards ?? [];
}

[DigitalBrain.App("ino", Accent = "#3D7EFF", Category = "Intelligence")]
public sealed class InoApp : DigitalBrain.App<InoAppState>
{
    public IDecisionMaker? DecisionMaker { get; set; }

    public IFilesVfsService? FilesVfs { get; set; }

    public IKnowledgeStore? KnowledgeStore { get; set; }

    public ITaskEngine? TaskEngine { get; set; }

    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "ino",
        "Ino",
        "3.5.0",
        [typeof(UserSettingsNeuron)],
        new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = new(
        "ino",
        "3.5.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, query databases, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Route or answer a message."),
                new("approve-card", "Approve or reject an inline LFP card."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
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
        var text = string.IsNullOrWhiteSpace(request.EffectiveMessage) ? "hello" : request.EffectiveMessage.Trim();
        var memory = TryGetMemory();
        var recalled = memory is null ? [] : await memory.Recall(text, 3);
        var settings = await Get<IUserSettings>("settings").Read();

        if (text.StartsWith('/'))
        {
            await HandleSlashCommandAsync(text, settings);
            return;
        }

        var decision = await InoIntentRouter.DecideIntentAsync(text, DecisionMaker, TryGetApp, TryGetAppStore);
        switch (decision.Choice)
        {
            case "app.install":
                Set(InoViewRenderer.BuildAppInstallLinkState(State, text));
                return;
            case "connection.string":
                await ConnectDb(new InoConnectDb(text, "main"));
                return;
            case "photocraft":
                Set(await InoStudioHandler.ExecuteAsync(
                    State,
                    "photocraft",
                    text,
                    settings,
                    recalled,
                    memory,
                    KnowledgeStore,
                    FilesVfs,
                    TryGetArtCraftStudio(),
                    TryGetMcpServer,
                    TryGetApp,
                    (err, reason) => Swallow(err, reason)));
                return;
            case "filmcraft":
                Set(await InoStudioHandler.ExecuteAsync(
                    State,
                    "filmcraft",
                    text,
                    settings,
                    recalled,
                    memory,
                    KnowledgeStore,
                    FilesVfs,
                    TryGetArtCraftStudio(),
                    TryGetMcpServer,
                    TryGetApp,
                    (err, reason) => Swallow(err, reason)));
                return;
            case "chart.mutation":
                Set(await InoDatabaseChartHandler.ApplyChartMutationOrFilterAsync(State, text, settings, memory));
                return;
            case "chart.question":
                Set(await InoDatabaseChartHandler.AnswerChartQuestionAsync(
                    State,
                    text,
                    settings,
                    recalled,
                    memory,
                    (prompt, chart, table) => TryGenerateReplyAsync(prompt, settings, recalled, chart, table)));
                return;
            case "database.query":
                await QueryDb(new InoQueryDb(text));
                return;
            case "settings.navigate":
                if (InoSettingsHandler.TryResolveSettingsNavigation(
                    text,
                    out var settingsSection,
                    out var settingsSubPage,
                    out var settingsSheet,
                    out var requiresSettingsApproval))
                {
                    Set(await InoSettingsHandler.ExecuteAsync(
                        State,
                        text,
                        settingsSection,
                        settingsSubPage,
                        settingsSheet,
                        requiresSettingsApproval,
                        settings,
                        recalled,
                        memory,
                        TryGetApp,
                        (err, reason) => Swallow(err, reason)));
                    return;
                }
                break;
            case "file.resource":
                if (InoFilesHandler.TryParseFileIntent(text, out var fileIntent))
                {
                    Set(await InoFilesHandler.ExecuteAsync(
                        State,
                        fileIntent,
                        FilesVfs,
                        settings,
                        recalled,
                        memory,
                        KnowledgeStore,
                        prompt => TryGenerateReplyAsync(prompt, settings, recalled)));
                    return;
                }
                break;
        }

        var routeHint = text.StartsWith("open ", StringComparison.OrdinalIgnoreCase)
            ? $"routed:{text[5..].Trim()}.open"
            : $"agent:{settings.DefaultModel}";
        var contextSummary = recalled.Length > 0
            ? $" (recalled {recalled.Length} memory item(s): {recalled[0].Text})"
            : string.Empty;
        var activeChart = State.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
        var activeTable = State.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Rows is { Length: > 0 });

        var isCapabilityInquiry = InoCapabilityCatalog.IsCapabilityOrMetaInquiry(text);
        var liveCapabilityContext = isCapabilityInquiry
            ? await InoCapabilityCatalog.BuildLiveCapabilityContextAsync(
                TryGetApp,
                TryGetAppStore,
                FilesVfs,
                memory,
                KnowledgeStore,
                TaskEngine,
                State.ConnectedDatabase,
                settings.DefaultModel)
            : null;

        var generated = string.IsNullOrWhiteSpace(request.EffectiveMessage)
            ? null
            : await TryGenerateReplyAsync(text, settings, recalled, activeChart, activeTable, liveCapabilityContext);

        var reply = isCapabilityInquiry && !string.IsNullOrWhiteSpace(liveCapabilityContext)
            ? (generated is { Length: > 0 }
                ? $"Ino ({settings.DefaultModel}): {generated}\n\n{liveCapabilityContext}{contextSummary}"
                : $"Ino ({settings.DefaultModel}): Here is what I can do across your IntoChat OS brain:\n\n{liveCapabilityContext}{contextSummary}")
            : (generated is { Length: > 0 }
                ? $"Ino ({settings.DefaultModel}): {generated}{contextSummary}"
                : $"Ino ({settings.DefaultModel}): Answered '{text}'{contextSummary}");

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
            var cardId = $"card-{turns.Length}";
            var card = new InoCard(
                cardId,
                "approval",
                $"Approve: {text}",
                "Pre-filled LFP ApprovalCard ready for 1-click confirmation.",
                "neuron://local/os.confirmations/inbox",
                "pending",
                $"neuron://local/apps/ino#{cardId}");
            cards = [.. cards, card];
        }
        else if (text.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || text.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("tripletex", StringComparison.OrdinalIgnoreCase))
        {
            var cardId = $"secret-{turns.Length}";
            cards =
            [
                .. cards,
                new InoCard(
                    cardId,
                    "secret",
                    "Tripletex Session Token",
                    "Required to verify Q4 invoice ledger balances in Tripletex. Value routes directly to Secure Vault.",
                    "neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN",
                    "pending",
                    $"neuron://local/apps/ino#{cardId}"),
            ];
        }

        Set(State with
        {
            Title = title,
            Status = routeHint,
            Turns = turns,
            Cards = cards,
        });
    }

    private async Task HandleSlashCommandAsync(string rawCommand, UserSettingsState settings)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "new":
                New(new InoNew());
                return;
            case "clear":
                Set(State with { Turns = [], Cards = [], Status = "Session cleared" });
                return;
            case "pin":
                Set(State with { IsPinned = !State.IsPinned, Status = State.IsPinned ? "Unpinned session" : "Pinned session to Arc bar" });
                return;
            case "rename":
                var nextTitle = string.IsNullOrWhiteSpace(arg) ? "Renamed session" : arg;
                Set(State with { Title = nextTitle, Status = $"Renamed to '{nextTitle}'" });
                return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? "claude-sonnet-4-5" : arg;
                await Get<IUserSettings>("settings").SetProfile(settings.DisplayName, settings.Theme, nextModel);
                Set(State with { Status = $"Switched model to {nextModel}" });
                return;
            case "scenario":
            case "session":
                Set(InoViewRenderer.BuildScenarioSessionState(string.IsNullOrWhiteSpace(arg) ? "q4-renewal" : arg.ToLowerInvariant(), settings));
                return;
            default:
                Set(State with
                {
                    Status = $"Executed /{cmd}",
                    Turns = [.. State.Turns, new InoTurn(rawCommand, "Commands: `/scenario q4-renewal|morning-brief|invoice-check|first-run`, `/new`, `/rename <title>`, `/pin`, `/model <id>`, `/clear`.", $"slash:{cmd}", 0)],
                });
                return;
        }
    }

    [DigitalBrain.On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(await InoDatabaseChartHandler.ConnectDbAsync(State, request, TryGetMemory(), KnowledgeStore));
    }

    [DigitalBrain.On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(await InoDatabaseChartHandler.QueryDbAsync(State, request, TryGetMemory()));
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
        var cardId = $"app-{target}";
        var card = new InoCard(
            cardId,
            "applet",
            $"Live App: {target}",
            sentence,
            $"neuron://local/apps/{target}",
            "running",
            $"neuron://local/apps/ino#{cardId}");
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

    public View RenderSide() => InoViewRenderer.RenderSide(State);

    public View Render() => InoViewRenderer.Render(State);

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

        await ConnectDb(new InoConnectDb("Host=db.nordlys.internal;Database=nordlys_crm;Username=readonly"));
        await QueryDb(new InoQueryDb("SELECT opportunity, account, stage, amount FROM opportunities"));
        if (!State.ActiveCards.Any(c => c.Kind == "table" && c.Rows is { Length: > 0 })
            || !State.ActiveCards.Any(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 }))
        {
            throw new InvalidOperationException("Expected InoApp to execute SQL query and emit both table and chart cards.");
        }

        await Send(new InoSend("What are top 3 on the chart I see?"));
        if (!State.Turns[^1].Reply.Contains("Havbris pilot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected InoApp to answer follow-up question about the top 3 items on the visible chart.");
        }

        await Send(new InoSend("Switch the chart I see to a pie chart"));
        if (!State.ActiveCards.Any(c => c.Kind == "chart" && c.ChartKind == "pie"))
        {
            throw new InvalidOperationException("Expected InoApp to switch the active chart kind to pie.");
        }

        await Send(new InoSend("Remove background from portrait.png"));
        if (!State.ActiveCards.Any(c => c.Kind == "photocraft")
            || !State.Turns[^1].Reply.Contains("layer.removeBackground", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected InoApp to route photo background removal to PhotoCraft and emit a photocraft card.");
        }

        var snapshot = await memory.Read();
        if (snapshot.Episodes.Length == 0)
        {
            throw new InvalidOperationException("Expected InoApp to capture the conversation episode in MemoryNeuron.");
        }
    }

    private async Task<string?> TryGenerateReplyAsync(
        string text,
        UserSettingsState settings,
        MemoryRecallHit[] recalled,
        InoCard? activeChart = null,
        InoCard? activeTable = null,
        string? liveCapabilityContext = null)
    {
        try
        {
            var model = Get<IQwen35>("main");
            var chartContext = activeChart is { ChartPoints: { Length: > 0 } pts }
                ? $" Visible UI Chart '{activeChart.Title}' ({activeChart.ChartKind ?? "bar"}): "
                  + string.Join(", ", pts.Select((p, idx) => $"#{idx + 1} {p.Label} = €{p.Value:N0}")) + "."
                : string.Empty;
            var tableContext = activeTable is { Columns: { Length: > 0 } cols, Rows: { Length: > 0 } rows }
                ? $" Visible UI Table '{activeTable.Title}' columns [{string.Join(", ", cols)}], rows: "
                  + string.Join("; ", rows.Select(r => string.Join(" | ", r))) + "."
                : string.Empty;
            var capabilityContext = !string.IsNullOrWhiteSpace(liveCapabilityContext)
                ? $" Live Installed Brain Capabilities:\n{liveCapabilityContext}\n"
                : string.Empty;
            var systemPrompt =
                $"You are Ino, the personal AI assistant of IntoChat OS (user: {settings.DisplayName}, model: {settings.DefaultModel}). "
                + (recalled.Length > 0 ? $"Recalled memory: {string.Join("; ", recalled.Select(r => r.Text))}. " : string.Empty)
                + chartContext
                + tableContext
                + capabilityContext
                + " Answer concisely and accurately using the live brain capabilities, visible chart, and table data when asked.";
            var messages = new List<AiMessage>
            {
                new("system", [new AiText(systemPrompt)]),
            };
            foreach (var turn in State.Turns.TakeLast(6))
            {
                messages.Add(new("user", [new AiText(turn.UserMessage)]));
                messages.Add(new("assistant", [new AiText(turn.Reply)]));
            }
            messages.Add(new("user", [new AiText(text)]));
            var result = await model.Generate(new InferenceRequest(messages));
            var generated = string.Join(
                "\n",
                result.Messages
                    .SelectMany(m => m.Content)
                    .OfType<AiText>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
            return string.IsNullOrWhiteSpace(generated) ? null : generated;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Local model IQwen35 is optional when running InoApp in unit tests without Ollama.");
            return null;
        }
    }

    private IApp? TryGetApp(string appKey)
    {
        try
        {
            return Get<IApp>(appKey);
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"App neuron '{appKey}' is optional when running InoApp in standalone unit tests.");
            return null;
        }
    }

    private IAppStore? TryGetAppStore()
    {
        try
        {
            return Get<IAppStore>("store");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "AppStoreNeuron is optional when running InoApp in standalone unit tests.");
            return null;
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

    private IArtCraftStudio? TryGetArtCraftStudio()
    {
        try
        {
            return Get<IArtCraftStudio>("artcraft");
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "ArtCraftStudioNeuron is optional when running InoApp without ArtCraftModule registered.");
            return null;
        }
    }

    private IMcpServer? TryGetMcpServer(string mcpKey)
    {
        try
        {
            return Get<IMcpServer>(mcpKey);
        }
        catch (InvalidOperationException error)
        {
            Swallow(error, "McpServerNeuron is optional when running InoApp without McpModule registered.");
            return null;
        }
    }
}
