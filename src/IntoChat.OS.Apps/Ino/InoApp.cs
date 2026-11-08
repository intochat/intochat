/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, stop, smart, connect-db, query-db, new, new-conversation, voice, make

The brain's personal assistant. Every message starts a background run on one agent (IAgent) per conversation, which acts
through the Ino tool contracts; Ino follows the run through a set watch over ino.conversations and renders tool-result cards inline. Slash commands stay in the UI.
*/
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;

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

[DigitalBrain.Signal("ino.stop")]
public sealed record InoStop : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.smart")]
public sealed record InoSmart(bool On = true) : DigitalBrain.Signal;

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

    // Read from the settings and the approval inbox on open and on change; Render shows the last read.
    private bool _smart;
    private Approval[] _approvals = [];

    private const string SmartVariable = "ino.smart";
    private const string ApprovalsKey = "approvals/index";
    private const int SeenKept = 256;

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
                new("stop", "Stop the running turn."),
                new("smart", "Turn smart mode on or off."),
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
        _smart = IsSmart(settings);
        var status = request.Mode == "greet"
            ? $"Hello, {settings.DisplayName}! Ready on {settings.DefaultModel}."
            : $"Ready ({settings.DefaultModel})";
        // Turns from before the agent existed stay in the archive; the agent gets one note, once.
        var migrate = State.Turns.Length > 0 && string.IsNullOrEmpty(State.AgentKey);
        Set(State with { Status = status, AgentKey = migrate ? State.AgentKey : AgentKeyOf(State.ConversationId) });
        try
        {
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(AgentKeyOf(State.ConversationId));
            if (migrate)
            {
                // The key is set only once the note is in, so a failed Post is retried on the next open.
                await agent.Post(MigrationNote, MigrationId);
                Set(State with { AgentKey = AgentKeyOf(State.ConversationId) });
            }
            await EnsureConfiguredAsync(agent, settings);
            await FollowAsync(AgentKeyOf(State.ConversationId));
            var read = await agent.Read();
            _transcript = await agent.Transcript();
            Set(State with { Blocked = read.Blocked, BlockedReason = read.Blocked ? State.BlockedReason ?? "An interrupt holds the agent." : null });
            await ReconcileAsync(agent, State.ActiveRunId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent is optional when Ino runs without the AI module.");
        }
        await RefreshApprovalsAsync();
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
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(agentKey);
            await EnsureConfiguredAsync(agent, settings);
            await FollowAsync(agentKey);
            // The run goes on in the agent; its signals reach the [On] handlers below through the set watch.
            var runId = await agent.Start(AgentTurn.Text(text, model: string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim()));
            Set(State with { ActiveRunId = runId, Blocked = false, BlockedReason = null, Status = "Thinking" });
            await ReconcileAsync(agent, runId);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            Pause(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent could not start; the status line reports why.");
            Set(State with { Status = $"Ino could not answer: {error.Message}", ActiveRunId = null });
        }
    }

    // Nothing else wakes the profile writer: touching it makes its synapse on the settings and writes user.profile now.
    private async Task EnsureProfileContextAsync()
    {
        try { await Get<IProfileContextWriter>("ino/profile-writer").Ensure(); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The profile context is optional; the agent answers without it."); }
    }

    // The agent's own record settles what a missed signal would have: a run Ino holds that has ended (a fast reply, a signal published
    // before the watch moved, or one lost while the host was down) is closed, and a run the agent has that Ino lost is adopted.
    private async Task ReconcileAsync(IAgent agent, string? runId)
    {
        var read = await agent.Read();
        if (runId is null)
        {
            if (read.ActiveRun is { } running) { Set(State with { ActiveRunId = running.RunId, Status = "Thinking" }); }
            return;
        }
        if (read.ActiveRun?.RunId == runId) { return; }
        if (read.Runs.FirstOrDefault(run => run.RunId == runId) is not { } ended)
        {
            // Not active and not recorded (the agent was cleared): nothing is running.
            Set(State with { ActiveRunId = null, Status = State.Status == "Thinking" || State.Status.StartsWith("Using ", StringComparison.Ordinal) ? "Ready" : State.Status });
            return;
        }
        _transcript = await agent.Transcript();
        Set(State with
        {
            ActiveRunId = null,
            Status = ended.Status switch
            {
                AgentRunStatus.Completed => "Ready",
                AgentRunStatus.Cancelled => "Cancelled",
                AgentRunStatus.Running => State.Status,
                _ => $"Failed: {ended.Error}",
            },
        });
    }

    // The set watch follows the registry's current agent; naming it there moves the watch to this conversation.
    private async Task FollowAsync(string agentKey)
    {
        try { await Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Without the registry Ino starts the run but does not see its progress."); }
    }

    protected override void Watches(DigitalBrain.WatchBuilder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        On<TurnProgressed>(watch, "agent-progressed", OnTurnProgressed);
        On<TurnCompleted>(watch, "agent-completed", OnTurnCompleted);
        On<TurnFailed>(watch, "agent-failed", OnTurnFailed);
        On<AgentBlocked>(watch, "agent-blocked", OnAgentBlocked);
    }

    private static void On<TSignal>(DigitalBrain.WatchBuilder watch, string name, Func<TSignal, Task> handle) where TSignal : DigitalBrain.Signal
        => watch.OnEach<IInoConversations, InoConversationsChanged, IAgent, TSignal>(
            name,
            _ => IInoConversations.DefaultKey,
            (registry, _, _) => registry.Current(),
            handle);

    private bool IsCurrent(string agentId) => string.Equals(agentId, AgentKeyOf(State.ConversationId), StringComparison.Ordinal);

    public Task OnTurnProgressed(TurnProgressed progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        var seen = $"{progressed.RunId}:{progressed.Sequence}";
        // Progress is delivered at least once: a step already applied is skipped.
        if (!IsCurrent(progressed.AgentId) || (State.SeenProgress ?? []).Contains(seen, StringComparer.Ordinal)) { return Task.CompletedTask; }
        var state = State with { SeenProgress = [.. (State.SeenProgress ?? []).Append(seen).TakeLast(SeenKept)] };
        state = progressed.Kind switch
        {
            "tool-call" => state with { Status = $"Using {progressed.Tool}" },
            "tool-result" when progressed.Payload is { Length: > 0 } payload
                => state with { Cards = [.. state.ActiveCards, .. InoCards.FromToolCalls([new AgentToolCall(seen, progressed.Tool ?? "", "", payload)])] },
            _ => state,
        };
        Set(state);
        return Task.CompletedTask;
    }

    public async Task OnTurnCompleted(TurnCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!IsCurrent(completed.AgentId)) { return; }
        try { _transcript = await Get<IAgent>(completed.AgentId).Transcript(); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The reply stays in the agent; the next open reloads it."); }
        await RefreshApprovalsAsync();
        Set(State with { Status = "Ready", ActiveRunId = null });
    }

    public Task OnTurnFailed(TurnFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (!IsCurrent(failed.AgentId)) { return Task.CompletedTask; }
        if (failed.Error.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            Pause(failed.Error);
            Set(State with { ActiveRunId = null });
            return Task.CompletedTask;
        }
        Set(State with { ActiveRunId = null, Status = failed.Cancelled ? "Cancelled" : $"Failed: {failed.Error}" });
        return Task.CompletedTask;
    }

    public async Task OnAgentBlocked(AgentBlocked blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (!IsCurrent(blocked.Agent)) { return; }
        if (blocked.Blocked) { Pause(BlockedPrefix + "An interrupt holds the agent."); }
        else { Set(State with { Blocked = false, BlockedReason = null, Status = "Ready" }); }
        await RefreshApprovalsAsync();
    }

    [DigitalBrain.On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = Get<IAgent>(AgentKeyOf(State.ConversationId));
        if (State.ActiveRunId is null) { await ReconcileAsync(agent, null); }
        if (State.ActiveRunId is null) { return; }
        // Stopping first: the agent's TurnFailed (cancelled), which may arrive during Cancel, has the last word and clears the run.
        Set(State with { Status = "Stopping" });
        await agent.Cancel();
    }

    [DigitalBrain.On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").SetVariable(SmartVariable, request.On ? "on" : "off");
        _smart = IsSmart(settings);
    }

    private static bool IsSmart(UserSettingsState settings)
        => settings.Variables.Any(v => v.Key == SmartVariable && string.Equals(v.Value, "on", StringComparison.OrdinalIgnoreCase));

    private async Task RefreshApprovalsAsync()
    {
        try { _approvals = [.. await Get<IApprovals>(ApprovalsKey).Pending()]; }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Approvals are optional when the inbox is not reachable."); }
    }

    // Configure on first use and keep the idle agent's definition synchronized with user settings (persona & default model).
    private async Task EnsureConfiguredAsync(IAgent agent, UserSettingsState settings)
    {
        var current = await agent.Read();
        var definition = InoAgentDefinition.For(settings);
        if (current.Revision == 0)
        {
            try { await agent.Configure(definition, 0); }
            catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)) { _ = error; }
            // The tone observer follows this conversation's agent; an app cannot subscribe to it directly.
            try { await Get<IToneObserver>(ToneKeyOf(State.ConversationId)).Follow(AgentKeyOf(State.ConversationId)); }
            catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The tone is a refinement; the agent answers without it."); }
            return;
        }

        if (current.ActiveRun is null
            && (!string.Equals(current.Definition.Model?.Model, definition.Model?.Model, StringComparison.Ordinal)
                || !string.Equals(current.Definition.Instructions, definition.Instructions, StringComparison.Ordinal)))
        {
            try { await agent.Configure(definition, current.Revision); }
            catch (InvalidOperationException error) { Swallow(error, "A concurrent turn or configuration updated the agent first."); }
        }
    }

    private void Pause(string message)
        => Set(State with { Blocked = true, BlockedReason = message[BlockedPrefix.Length..], Status = "Paused" });

    private const string DataKey = "ino/data";

    private static string AgentKeyOf(string conversationId) => $"ino/conversations/{conversationId}";

    private static string ToneKeyOf(string conversationId) => $"observer/tone/{AgentKeyOf(conversationId)}";

    private async Task HandleSlashCommandAsync(string rawCommand, UserSettingsState settings)
    {
        var parts = rawCommand[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "new":
                await New(new InoNew());
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
                var nextModel = string.IsNullOrWhiteSpace(arg) ? "claude-sonnet-4-5" : arg.Trim();
                var updated = await Get<IUserSettings>("settings").SetProfile(settings.DisplayName, settings.Theme, nextModel);
                await EnsureProfileContextAsync();
                try { await EnsureConfiguredAsync(Get<IAgent>(AgentKeyOf(State.ConversationId)), updated); }
                catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The agent is optional when Ino runs without the AI module."); }
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
    public Task New(InoNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversation();
    }

    [DigitalBrain.On]
    public Task NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversation();
    }

    private async Task StartConversation()
    {
        var conversationId = $"c-{Guid.NewGuid():N}"[..10];
        _transcript = [];
        Set(new InoAppState(conversationId, NewTitle, "New conversation started", "idle", [])
        {
            AgentKey = AgentKeyOf(conversationId),
            GrantSummaries = State.GrantSummaries,
        });
        // The watch moves now, so the new conversation's first signals are not raced by the next Send.
        await FollowAsync(AgentKeyOf(conversationId));
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
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null) { return; }
        var settings = await Get<IUserSettings>("settings").Read();
        _smart = IsSmart(settings);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(AgentKeyOf(State.ConversationId)), settings); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The agent is optional when Ino runs without the AI module."); }
        Set(State with { Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
    }

    public View RenderSide() => InoViewRenderer.RenderSide(State);

    public View Render() => InoViewRenderer.Render(State, _transcript, _approvals, _smart);

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
