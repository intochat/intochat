/*
# Ino
Role: Shell
Shipped: true
Operations: open, send, stop, smart, connect-db, query-db, new, new-conversation, voice, make

The brain's personal assistant. Every message starts a background run on one agent (IAgent) per conversation, which acts
through the Ino tool contracts; Ino follows the run through a set watch over ino.conversations and renders tool-result cards inline. Slash commands stay in the UI.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Observers;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Chat;

namespace IntoChat.OS.Apps.Ino;

[DigitalBrain.Signal("ino.open")]
public sealed record InoOpen(string? Mode = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("ino.send")]
public sealed record InoSend(string? Message = null, string? Text = null, string? Value = null) : DigitalBrain.Signal
{
    // Value is the composer field's text, which a view submit adds to the emit.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EffectiveMessage => !string.IsNullOrWhiteSpace(Message) ? Message : !string.IsNullOrWhiteSpace(Text) ? Text : Value;
}

[DigitalBrain.Signal("ino.link")]
public sealed record InoPasteLink(Guid Request = default, string? App = null, string? Input = null, string? Text = null) : DigitalBrain.Signal;

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

[Neuron("app.ino")]
public sealed class InoApp : Screen<InoAppState>, IWithFilesVfs
{
    private const string BlockedPrefix = "Blocked: ";
    private const string NewTitle = "New chat";
    private const string MigrationNote = "Earlier conversation is in the archive.";
    private const string MigrationId = "migration";
    private const string SmartVariable = "ino.smart";
    private const string ApprovalsKey = "approvals/index";
    private const string DataKey = "ino/data";
    private const int SeenKept = 256;

    // Render cannot await: the last transcript the agent answered, refreshed after each turn.
    private AiMessage[] _transcript = [];
    // Read from the settings and the approval inbox on open and on change; Render shows the last read.
    private bool _smart;
    private Approval[] _approvals = [];

    public IFilesVfsService? FilesVfs { get; set; }

    private static void Swallow(Exception error, string reason)
    {
        _ = error;
        _ = reason;
    }

    public const string AppName = "ino";

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
                new("link", "Post a pasted workspace link: its app, opaque input and text."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg",
            Dock: 1,
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

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        try { await Follow<IUserSettings, UserSettingsChanged>("settings"); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Settings synapse is optional."); }
    }

    [DigitalBrain.Neurons.On]
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
        var agentKey = AgentKeyOf(State.ConversationId);
        await Show(State with { Status = status, AgentKey = migrate ? State.AgentKey : agentKey });
        try
        {
            await EnsureProfileContextAsync();
            var agent = Get<IAgent>(agentKey);
            if (migrate)
            {
                // The key is set only once the note is in, so a failed Post is retried on the next open.
                await agent.Post(MigrationNote, MigrationId);
                await Show(State with { AgentKey = agentKey });
            }
            await EnsureConfiguredAsync(agent, settings);
            await FollowAsync(agentKey);
            var read = await agent.Read();
            _transcript = await agent.Transcript();
            await Show(State with { Blocked = read.Blocked, BlockedReason = read.Blocked ? State.BlockedReason ?? "An interrupt holds the agent." : null });
            await ReconcileAsync(agent, State.ActiveRunId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent is optional when Ino runs without the AI module.");
        }
        await RefreshApprovalsAsync();
        await RefreshGrantsAsync();
    }

    [DigitalBrain.Neurons.On]
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
            await Show(State with { GrantSummaries = [.. grants.Where(g => g.AppId == assistant && !g.Revoked).Select(g => $"{g.Scope} · {g.Target}")] });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The grants neuron is optional when Ino runs without the identity module.");
        }
    }

    // A pasted link is remembered in Ino's records neuron (the chat reads its card from there) and posted to the agent under its plain id.
    [DigitalBrain.Neurons.On]
    public async Task Link(InoPasteLink request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Request == Guid.Empty || !ChatAppCard.IsAppName(request.App)) { return; }
        var input = request.Input ?? "";
        if (input.Length > ChatAppCard.MaxInput) { throw new ArgumentException("The link input is too long."); }
        var id = request.Request.ToString("N");
        var records = Get<IInoRecords>(IInoRecords.Key);
        var kept = await records.Read();
        if (!kept.Links.Any(link => link.Request == id))
        {
            await records.Write(kept with { Links = [.. kept.Links.TakeLast(ChatAppCard.MaxLinksKept - 1), new InoLink(id, request.App!, input)] });
        }
        await Get<IAgent>(AgentKeyOf(State.ConversationId)).Post(string.IsNullOrWhiteSpace(request.Text) ? request.App! : request.Text, ChatAppCard.LinkPostId(request.Request));
    }

    [DigitalBrain.Neurons.On]
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
        if (await IsComputeBudgetExceededAsync())
        {
            await Show(State with
            {
                Status = "Monthly compute cap exceeded. Increase cap in Settings -> Billing (os://window/settings?section=billing).",
                ActiveRunId = null,
            });
            return;
        }

        var agentKey = AgentKeyOf(State.ConversationId);
        await Show(State with
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
            var runId = await agent.Start(AgentTurn.Text(text, model: string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim()));
            await Show(State with { ActiveRunId = runId, Blocked = false, BlockedReason = null, Status = "Thinking" });
            await ReconcileAsync(agent, runId);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            await Pause(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent could not start; the status line reports why.");
            await Show(State with { Status = $"Ino could not answer: {error.Message}", ActiveRunId = null });
        }
    }

    // Nothing else wakes the profile writer: touching it makes its synapse on the settings and writes user.profile now.
    private async Task EnsureProfileContextAsync()
    {
        try { await Get<IProfileContextWriter>("ino/profile-writer").Ensure(); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The profile context is optional; the agent answers without it."); }
    }

    private async Task<bool> IsComputeBudgetExceededAsync()
    {
        try { return (await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Read()).BudgetExceeded; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Compute metering is optional when Ino runs without the Flutter compute neuron.");
            return false;
        }
    }

    // The agent's own record settles what a missed signal would have: a run Ino holds that has ended is closed, and a run the agent has that Ino lost is adopted.
    private async Task ReconcileAsync(IAgent agent, string? runId)
    {
        var read = await agent.Read();
        if (runId is null)
        {
            if (read.ActiveRun is { } running) { await Show(State with { ActiveRunId = running.RunId, Status = "Thinking" }); }
            return;
        }
        if (read.ActiveRun?.RunId == runId) { return; }
        if (read.Runs.FirstOrDefault(run => run.RunId == runId) is not { } ended)
        {
            await Show(State with { ActiveRunId = null, Status = State.Status == "Thinking" || State.Status.StartsWith("Using ", StringComparison.Ordinal) ? "Ready" : State.Status });
            return;
        }
        _transcript = await agent.Transcript();
        await Show(State with
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

        try
        {
            await Follow<IAgent, TurnProgressed>(agentKey);
            await Follow<IAgent, TurnCompleted>(agentKey);
            await Follow<IAgent, TurnFailed>(agentKey);
            await Follow<IAgent, AgentBlocked>(agentKey);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Agent synapses are optional."); }
    }

    private bool IsCurrent(string agentId) => string.Equals(agentId, AgentKeyOf(State.ConversationId), StringComparison.Ordinal);

    [DigitalBrain.Neurons.On]
    public async Task OnTurnProgressed(TurnProgressed progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        var seen = $"{progressed.RunId}:{progressed.Sequence}";
        if (!IsCurrent(progressed.AgentId) || (State.SeenProgress ?? []).Contains(seen, StringComparer.Ordinal)) { return; }
        var state = State with { SeenProgress = [.. (State.SeenProgress ?? []).Append(seen).TakeLast(SeenKept)] };
        await Show(progressed.Kind switch
        {
            "tool-call" => state with { Status = $"Using {progressed.Tool}" },
            "tool-result" when progressed.Payload is { Length: > 0 } payload
                => state with { Cards = [.. state.ActiveCards, .. InoCards.FromToolCalls([new AgentToolCall(seen, progressed.Tool ?? "", "", payload)])] },
            _ => state,
        });
    }

    [DigitalBrain.Neurons.On]
    public async Task OnTurnCompleted(TurnCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!IsCurrent(completed.AgentId)) { return; }
        try { _transcript = await Get<IAgent>(completed.AgentId).Transcript(); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The reply stays in the agent; the next open reloads it."); }
        try { await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Record("ino", Math.Max(1_000L, (completed.Reply?.Length ?? 0) * 25L)); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "Compute metering is optional when Ino runs without the Flutter compute neuron."); }
        await RefreshApprovalsAsync();
        await Show(State with { Status = "Ready", ActiveRunId = null });
    }

    [DigitalBrain.Neurons.On]
    public async Task OnTurnFailed(TurnFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (!IsCurrent(failed.AgentId)) { return; }
        if (failed.Error.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            await Pause(failed.Error);
            await Show(State with { ActiveRunId = null });
            return;
        }
        await Show(State with { ActiveRunId = null, Status = failed.Cancelled ? "Cancelled" : $"Failed: {failed.Error}" });
    }

    [DigitalBrain.Neurons.On]
    public async Task OnAgentBlocked(AgentBlocked blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (!IsCurrent(blocked.Agent)) { return; }
        if (blocked.Blocked) { await Pause(BlockedPrefix + "An interrupt holds the agent."); }
        else { await Show(State with { Blocked = false, BlockedReason = null, Status = "Ready" }); }
        await RefreshApprovalsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var agent = Get<IAgent>(AgentKeyOf(State.ConversationId));
            if (State.ActiveRunId is null) { await ReconcileAsync(agent, null); }
            if (State.ActiveRunId is null) { return; }
            await Show(State with { Status = "Stopping" });
            await agent.Cancel();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The agent is optional when Ino runs without the AI module.");
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task Smart(InoSmart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _smart = IsSmart(await Get<IUserSettings>("settings").SetVariable(SmartVariable, request.On ? "on" : "off"));
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

    private Task Pause(string message)
        => Show(State with { Blocked = true, BlockedReason = message[BlockedPrefix.Length..], Status = "Paused" });

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
                await Show(State with { Turns = [], Cards = [], Status = "Session cleared" });
                return;
            case "pin":
                await Show(State with { IsPinned = !State.IsPinned, Status = State.IsPinned ? "Unpinned session" : "Pinned session to Arc bar" });
                return;
            case "rename":
                var nextTitle = string.IsNullOrWhiteSpace(arg) ? "Renamed session" : arg;
                await Show(State with { Title = nextTitle, Status = $"Renamed to '{nextTitle}'" });
                return;
            case "model":
                var nextModel = string.IsNullOrWhiteSpace(arg) ? "claude-sonnet-4-5" : arg.Trim();
                var updated = await Get<IUserSettings>("settings").SetProfile(settings.DisplayName, settings.Theme, nextModel);
                await EnsureProfileContextAsync();
                try { await EnsureConfiguredAsync(Get<IAgent>(AgentKeyOf(State.ConversationId)), updated); }
                catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The agent is optional when Ino runs without the AI module."); }
                await Show(State with { Status = $"Switched model to {nextModel}" });
                return;
            case "scenario":
            case "session":
                await Show(InoViewRenderer.BuildScenarioSessionState(string.IsNullOrWhiteSpace(arg) ? "q4-renewal" : arg.ToLowerInvariant(), settings));
                return;
            default:
                await Show(State with
                {
                    Status = $"Executed /{cmd}",
                    Turns = [.. State.Turns, new InoTurn(rawCommand, "Commands: `/scenario q4-renewal|morning-brief|invoice-check|first-run`, `/new`, `/rename <title>`, `/pin`, `/model <id>`, `/clear`.", $"slash:{cmd}", 0)],
                });
                return;
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task ConnectDb(InoConnectDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var data = Get<IInoData>(DataKey);
            var result = await data.Connect(request.ConnectionString ?? string.Empty);
            await Show(State with
            {
                ConnectedDatabase = (await data.Read()).ConnectedDatabase ?? State.ConnectedDatabase,
                Status = result.Text,
                Cards = result.Card is { } card ? [.. State.ActiveCards, card] : State.Cards,
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The connection failed; the status line reports why.");
            await Show(State with { Status = $"Could not connect: {error.Message}" });
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task QueryDb(InoQueryDb request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sql = string.IsNullOrWhiteSpace(request.Sql) ? InoDataCards.DefaultSql : request.Sql.Trim();
        var database = string.IsNullOrWhiteSpace(State.ConnectedDatabase) ? InoDataCards.DefaultDatabase : State.ConnectedDatabase;
        DataQueryResult result;
        try { result = await DataAnalyticsEngine.ExecuteAsync(sql, database, FilesVfs); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "The query failed; the status line reports why.");
            await Show(State with { Status = $"Could not run the query: {error.Message}" });
            return;
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6);
        }
        var (table, chart) = InoDataCards.Build(result, database, $"table-{State.ActiveCards.Length + 1}", $"chart-{State.ActiveCards.Length + 2}");
        await Show(State with
        {
            ConnectedDatabase = database,
            Status = $"Visualized {result.Rows.Length} row(s) & {result.ChartPoints.Length} chart point(s) from {database}",
            Cards = [.. State.ActiveCards, table, chart],
        });
    }

    [DigitalBrain.Neurons.On]
    public Task New(InoNew request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversation();
    }

    [DigitalBrain.Neurons.On]
    public Task NewConversation(InoNewConversation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartConversation();
    }

    private async Task StartConversation()
    {
        var conversationId = $"c-{Guid.NewGuid():N}"[..10];
        _transcript = [];
        await Show(new InoAppState(conversationId, NewTitle, "New conversation started", "idle", [])
        {
            AgentKey = AgentKeyOf(conversationId),
            GrantSummaries = State.GrantSummaries,
        });
        await FollowAsync(AgentKeyOf(conversationId));
    }

    [DigitalBrain.Neurons.On]
    public async Task Voice(InoVoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(State with { VoiceStatus = "recording", Status = "Listening..." });
    }

    [DigitalBrain.Neurons.On]
    public async Task Make(InoMake request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sentence = string.IsNullOrWhiteSpace(request.Sentence) ? "Update focused app" : request.Sentence.Trim();
        var target = request.Focus is { Length: > 0 } ? request.Focus[0] : "scratch";
        if (TryGetOptional<IAppStudio>("main") is { } studio)
        {
            try { target = (await studio.Build(sentence)).Name; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "AppStudio build requires a live model and sandbox; falling back to local capture.");
            }
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6);
        }
        var cardId = $"app-{target}";
        await Show(State with
        {
            Status = $"Applied '{sentence}' to {target}",
            Cards = [.. State.ActiveCards, new InoCard(cardId, "applet", $"Live App: {target}", sentence, $"neuron://local/apps/{target}", "running", $"neuron://local/apps/ino#{cardId}")],
        });
    }

    [DigitalBrain.Neurons.On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null) { return; }
        var settings = await Get<IUserSettings>("settings").Read();
        _smart = IsSmart(settings);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(AgentKeyOf(State.ConversationId)), settings); }
        catch (Exception error) when (error is not OperationCanceledException) { Swallow(error, "The agent is optional when Ino runs without the AI module."); }
        if (!State.Status.StartsWith("Switched model to ", StringComparison.Ordinal))
        {
            await Show(State with { Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
        }
    }

    public new InoAppState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => InoViewRenderer.RenderSide(State);
    protected override View Render(InoAppState state) => InoViewRenderer.Render(state, _transcript, _approvals, _smart);

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            Swallow(error, $"{typeof(T).Name} is optional when running in isolated unit tests.");
            return null;
        }
    }
}
