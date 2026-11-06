/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, approve-card, connect-db, query-db, new, new-conversation, voice, make

The brain's personal assistant. Every message goes to one agent (IAgent) per conversation, which acts
through the Ino tool contracts; tool results that carry a card render inline. Slash commands stay in the UI.
*/
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

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

[DigitalBrain.App("ino", Accent = "#3D7EFF", Category = "Intelligence")]
public sealed class InoApp : DigitalBrain.App<InoAppState>
{
    private const string BlockedPrefix = "Blocked: ";
    private const string NewTitle = "New chat";
    private const string MigrationNote = "Earlier conversation is in the archive.";
    private const string MigrationId = "migration";

    // Render cannot await: the last transcript the agent answered, refreshed after each turn.
    private AiMessage[] _transcript = [];

    public IFilesVfsService? FilesVfs { get; set; }

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(InoApp));

    public static AppBundle Bundle { get; } = new(
        "ino",
        "3.5.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, query databases, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Send a message to Ino's agent."),
                new("approve-card", "Approve or reject an inline LFP card."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("voice", "Begin voice input."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Toolbar:
            [
                new("send", Kind: "text", Label: "Message your brain"),
                new("new-conversation", Icon: "chat", Label: "New conversation"),
                new("voice", Icon: "mic", Label: "Voice"),
            ]),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(InoApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        var status = request.Mode == "greet"
            ? $"Hello, {settings.DisplayName}! Ready on {settings.DefaultModel}."
            : $"Ready ({settings.DefaultModel})";
        // Turns from before the agent existed stay in the archive; the agent gets one note, once.
        var migrate = State.Turns.Length > 0 && string.IsNullOrEmpty(State.AgentKey);
        Set(State with { Status = status, AgentKey = migrate ? State.AgentKey : AgentKeyOf(State.ConversationId) });
        try
        {
            // Nothing else wakes the profile writer: touching it makes its synapse on the settings and writes user.profile now.
            await Get<IProfileContextWriter>("ino/profile-writer").Ensure();
            var agent = Get<IAgent>(AgentKeyOf(State.ConversationId));
            if (migrate)
            {
                // The key is set only once the note is in, so a failed Post is retried on the next open.
                await agent.Post(MigrationNote, MigrationId);
                Set(State with { AgentKey = AgentKeyOf(State.ConversationId) });
            }
            var read = await agent.Read();
            _transcript = await agent.Transcript();
            Set(State with { Blocked = read.Blocked, BlockedReason = read.Blocked ? State.BlockedReason ?? "An interrupt holds the agent." : null });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent is optional when Ino runs without the AI module.");
        }
        await RefreshGrantsAsync();
    }

    [DigitalBrain.On("grants/default", Contract = typeof(IGrantsNeuron))]
    public Task OnGrantsChanged(GrantsChanged? changed)
    {
        _ = changed; // any version change re-reads the list
        return RefreshGrantsAsync();
    }

    private async Task RefreshGrantsAsync()
    {
        try
        {
            var grants = await Get<IGrantsNeuron>(IGrantsNeuron.DefaultKey).List();
#pragma warning disable CS0618 // the AppId every agent tool call runs under (see ContractActions.Marked)
            var assistant = DigitalBrain.Capabilities.CapabilityCallers.AssistantApp;
#pragma warning restore CS0618
            Set(State with
            {
                GrantSummaries = [.. grants.Where(g => g.AppId == assistant && !g.Revoked).Select(g => $"{g.Scope} · {g.Target}")],
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The grants neuron is optional when Ino runs without the identity module.");
        }
    }

    [DigitalBrain.On]
    public async Task Send(InoSend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = string.IsNullOrWhiteSpace(request.EffectiveMessage) ? "hello" : request.EffectiveMessage.Trim();
        var settings = await Get<IUserSettings>("settings").Read();
        if (text.StartsWith('/'))
        {
            await HandleSlashCommandAsync(text, settings);
            return;
        }

        var agentKey = AgentKeyOf(State.ConversationId);
        Set(State with
        {
            AgentKey = agentKey,
            Title = State.Title == NewTitle ? (text.Length <= 52 ? text : text[..52]) : State.Title,
            Status = "Thinking",
        });
        try
        {
            var agent = Get<IAgent>(agentKey);
            await EnsureConfiguredAsync(agent, settings);
            await foreach (var step in agent.Run(AgentTurn.Text(text)))
            {
                Apply(step);
            }
            _transcript = await agent.Transcript();
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            Pause(error.Message);
        }
        catch (OperationCanceledException error)
        {
            Swallow(error, "The run was cancelled; the status line reports it.");
            Set(State with { Status = "Cancelled" });
        }
        catch (Exception error)
        {
            Swallow(error, "The agent could not answer; the status line reports why.");
            Set(State with { Status = $"Ino could not answer: {error.Message}" });
        }
        finally
        {
            Set(State with { ActiveRunId = null });
        }
    }

    // Configure on first use; a revision conflict means another caller configured it first.
    private static async Task EnsureConfiguredAsync(IAgent agent, UserSettingsState settings)
    {
        if ((await agent.Read()).Revision != 0) { return; }
        try { await agent.Configure(InoAgentDefinition.For(settings), 0); }
        catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)) { _ = error; }
    }

    private void Apply(AgentTurnEvent step)
    {
        switch (step)
        {
            case AgentTurnEvent.Started started:
                Set(State with { ActiveRunId = started.RunId, Blocked = false, BlockedReason = null, Status = "Thinking" });
                break;
            case AgentTurnEvent.ToolStarted tool:
                Set(State with { Status = $"Using {tool.Name}" });
                break;
            case AgentTurnEvent.ToolCompleted tool:
                Set(State with { Cards = [.. State.ActiveCards, .. InoCards.FromToolCalls([new AgentToolCall(tool.CallId, tool.Name, "", tool.Result)])] });
                break;
            case AgentTurnEvent.ToolFailed tool:
                Set(State with { Status = $"{tool.Name} failed: {tool.Message}" });
                break;
            case AgentTurnEvent.Completed:
                Set(State with { Status = "Ready" });
                break;
            case AgentTurnEvent.Failed failed when failed.Message.StartsWith(BlockedPrefix, StringComparison.Ordinal):
                Pause(failed.Message);
                break;
            case AgentTurnEvent.Failed failed:
                Set(State with { Status = failed.Cancelled ? "Cancelled" : $"Failed: {failed.Message}" });
                break;
        }
    }

    private void Pause(string message)
        => Set(State with { Blocked = true, BlockedReason = message[BlockedPrefix.Length..], Status = "Paused" });

    private const string DataKey = "ino/data";

    private static string AgentKeyOf(string conversationId) => $"ino/conversations/{conversationId}";

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
                _transcript = [];
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
        try
        {
            var data = Get<IInoData>(DataKey);
            var result = await data.Connect(request.ConnectionString ?? string.Empty);
            Set(State with
            {
                ConnectedDatabase = (await data.Read()).ConnectedDatabase ?? State.ConnectedDatabase,
                Status = result.Text,
                Cards = result.Card is { } card ? [.. State.ActiveCards, card] : State.Cards,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The connection failed; the status line reports why.");
            Set(State with { Status = $"Could not connect: {error.Message}" });
        }
    }

    [DigitalBrain.On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Reads through this app's own files service, so a host-injected workspace stays the one source of datasets.
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try
        {
            result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The query failed; the status line reports why.");
            Set(State with { Status = $"Could not run the query: {error.Message}" });
            return;
        }
        if (TryGetMemory() is { } memory)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6);
        }
        var (table, chart) = InoDataCards.Build(result, database, $"table-{State.ActiveCards.Length + 1}", $"chart-{State.ActiveCards.Length + 2}");
        Set(State with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {result.Rows.Length} row(s) & {result.ChartPoints.Length} chart point(s) from {database}",
            Cards = [.. State.ActiveCards, table, chart],
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
        StartConversation();
    }

    [DigitalBrain.On]
    public void NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StartConversation();
    }

    private void StartConversation()
    {
        var conversationId = $"c-{Guid.NewGuid():N}"[..10];
        _transcript = [];
        Set(new InoAppState(conversationId, NewTitle, "New conversation started", "idle", [])
        {
            AgentKey = AgentKeyOf(conversationId),
            GrantSummaries = State.GrantSummaries,
        });
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

    public View Render() => InoViewRenderer.Render(State, _transcript);

    [DigitalBrain.Scenario("Querying a connected database renders a table and a chart card")]
    public async Task VerifyDatabaseCardsScenario()
    {
        await ConnectDb(new InoConnectDb("Host=db.nordlys.internal;Database=nordlys_crm;Username=readonly"));
        await QueryDb(new InoQueryDb("SELECT opportunity, account, stage, amount FROM opportunities"));
        if (!State.ActiveCards.Any(c => c.Kind == "table" && c.Rows is { Length: > 0 })
            || !State.ActiveCards.Any(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 }))
        {
            throw new InvalidOperationException("Expected InoApp to execute SQL query and emit both table and chart cards.");
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
