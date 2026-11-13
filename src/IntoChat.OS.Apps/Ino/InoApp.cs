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
using DigitalBrain.Log;
using Microsoft.Extensions.Logging.Abstractions;

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
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Settings synapse is optional."); }
    }

    [DigitalBrain.Neurons.On]
    public async Task Open(InoOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = await Get<IUserSettings>("settings").Read();
        _smart = IsSmart(settings);
        var (nextState, migrate) = InoDecisions.DecideOpen(State, request.Mode, settings.DisplayName, settings.DefaultModel);
        var agentKey = InoDecisions.AgentKeyOf(State.ConversationId);
        await Show(nextState);
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
            await Show(InoDecisions.DecideAgentRead(State, read.Blocked, State.BlockedReason));
            await ReconcileAsync(agent, State.ActiveRunId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "The agent is optional when Ino runs without the AI module.");
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
            await Show(InoDecisions.DecideGrants(State, grants, assistant));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "The grants neuron is optional when Ino runs without the identity module.");
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
            await Show(InoDecisions.DecideComputeBudgetExceeded(State));
            return;
        }

        await Show(InoDecisions.DecideBeforeSend(State, text));
        try
        {
            await EnsureProfileContextAsync();
            var agentKey = InoDecisions.AgentKeyOf(State.ConversationId);
            var agent = Get<IAgent>(agentKey);
            await EnsureConfiguredAsync(agent, settings);
            await FollowAsync(agentKey);
            var runId = await agent.Start(AgentTurn.Text(text, model: string.IsNullOrWhiteSpace(settings.DefaultModel) ? null : settings.DefaultModel.Trim()));
            await Show(InoDecisions.DecideSendStarted(State, runId));
            await ReconcileAsync(agent, runId);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(BlockedPrefix, StringComparison.Ordinal))
        {
            await Pause(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "The agent could not start; the status line reports why.");
            await Show(State with { Status = $"Ino could not answer: {error.Message}", ActiveRunId = null });
        }
    }

    // Nothing else wakes the profile writer: touching it makes its synapse on the settings and writes user.profile now.
    private async Task EnsureProfileContextAsync()
    {
        try { await Get<IProfileContextWriter>("ino/profile-writer").Ensure(); }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The profile context is optional; the agent answers without it."); }
    }

    private async Task<bool> IsComputeBudgetExceededAsync()
    {
        try { return (await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Read()).BudgetExceeded; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "Compute metering is optional when Ino runs without the Flutter compute neuron.");
            return false;
        }
    }

    // The agent's own record settles what a missed signal would have: a run Ino holds that has ended is closed, and a run the agent has that Ino lost is adopted.
    private async Task ReconcileAsync(IAgent agent, string? runId)
    {
        var read = await agent.Read();
        var next = InoDecisions.DecideReconcile(State, read.ActiveRun, read.Runs, runId, out var reloadTranscript);
        if (reloadTranscript)
        {
            _transcript = await agent.Transcript();
        }
        await Show(next);
    }

    // The set watch follows the registry's current agent; naming it there moves the watch to this conversation.
    private async Task FollowAsync(string agentKey)
    {
        try { await Get<IInoConversations>(IInoConversations.DefaultKey).Use(agentKey); }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Without the registry Ino starts the run but does not see its progress."); }

        try
        {
            await Follow<IAgent, TurnProgressed>(agentKey);
            await Follow<IAgent, TurnCompleted>(agentKey);
            await Follow<IAgent, TurnFailed>(agentKey);
            await Follow<IAgent, AgentBlocked>(agentKey);
        }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Agent synapses are optional."); }
    }

    private bool IsCurrent(string agentId) => string.Equals(agentId, InoDecisions.AgentKeyOf(State.ConversationId), StringComparison.Ordinal);

    [DigitalBrain.Neurons.On]
    public async Task OnTurnProgressed(TurnProgressed progressed)
    {
        ArgumentNullException.ThrowIfNull(progressed);
        if (!IsCurrent(progressed.AgentId)) { return; }
        var next = InoDecisions.DecideTurnProgressed(State, progressed);
        if (next != State)
        {
            await Show(next);
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnTurnCompleted(TurnCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (!IsCurrent(completed.AgentId)) { return; }
        try { _transcript = await Get<IAgent>(completed.AgentId).Transcript(); }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The reply stays in the agent; the next open reloads it."); }
        try { await Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>("compute").Record("ino", Math.Max(1_000L, (completed.Reply?.Length ?? 0) * 25L)); }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Compute metering is optional when Ino runs without the Flutter compute neuron."); }
        await RefreshApprovalsAsync();
        await Show(InoDecisions.DecideTurnCompleted(State));
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
        await Show(InoDecisions.DecideTurnFailed(State, failed));
    }

    [DigitalBrain.Neurons.On]
    public async Task OnAgentBlocked(AgentBlocked blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (!IsCurrent(blocked.Agent)) { return; }
        if (blocked.Blocked) { await Pause(BlockedPrefix + "An interrupt holds the agent."); }
        else { await Show(InoDecisions.DecideAgentBlocked(State, blocked)); }
        await RefreshApprovalsAsync();
    }

    [DigitalBrain.Neurons.On]
    public async Task Stop(InoStop request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var agent = Get<IAgent>(InoDecisions.AgentKeyOf(State.ConversationId));
            if (State.ActiveRunId is null) { await ReconcileAsync(agent, null); }
            if (State.ActiveRunId is null) { return; }
            await Show(InoDecisions.DecideStop(State));
            await agent.Cancel();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "The agent is optional when Ino runs without the AI module.");
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
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "Approvals are optional when the inbox is not reachable."); }
    }

    // Configure on first use and keep the idle agent's definition synchronized with user settings (persona & default model).
    private async Task EnsureConfiguredAsync(IAgent agent, UserSettingsState settings)
    {
        var current = await agent.Read();
        var definition = InoAgentDefinition.For(settings);
        if (current.Revision == 0)
        {
            try { await agent.Configure(definition, 0); }
            catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.OrdinalIgnoreCase)) { NullLogger.Instance.LogSwallowed(error, "Agent configure revision conflict; concurrent configure occurred."); }
            try { await Get<IToneObserver>(ToneKeyOf(State.ConversationId)).Follow(AgentKeyOf(State.ConversationId)); }
            catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The tone is a refinement; the agent answers without it."); }
            return;
        }

        if (current.ActiveRun is null
            && (!string.Equals(current.Definition.Model?.Model, definition.Model?.Model, StringComparison.Ordinal)
                || !string.Equals(current.Definition.Instructions, definition.Instructions, StringComparison.Ordinal)))
        {
            try { await agent.Configure(definition, current.Revision); }
            catch (InvalidOperationException error) { NullLogger.Instance.LogSwallowed(error, "A concurrent turn or configuration updated the agent first."); }
        }
    }

    private Task Pause(string message)
        => Show(State with { Blocked = true, BlockedReason = message[BlockedPrefix.Length..], Status = "Paused" });

    private static string AgentKeyOf(string conversationId) => InoDecisions.AgentKeyOf(conversationId);

    private static string ToneKeyOf(string conversationId) => InoDecisions.ToneKeyOf(conversationId);

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
                catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The agent is optional when Ino runs without the AI module."); }
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
            await Show(InoDecisions.DecideConnectDb(State, (await data.Read()).ConnectedDatabase, result.Text, result.Card));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            NullLogger.Instance.LogSwallowed(error, "The connection failed; the status line reports why.");
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
            NullLogger.Instance.LogSwallowed(error, "The query failed; the status line reports why.");
            await Show(State with { Status = $"Could not run the query: {error.Message}" });
            return;
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await memory.CaptureEpisode("ino.query", $"SQL: {sql} | Rows: {result.Rows.Length} | Chart: {result.ChartTitle}", importance: 6);
        }
        await Show(InoDecisions.DecideQueryDbSuccess(State, result, database));
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
        var conversationId = InoDecisions.MintConversationId();
        _transcript = [];
        await Show(InoDecisions.DecideNew(State, conversationId));
        await FollowAsync(InoDecisions.AgentKeyOf(conversationId));
    }

    [DigitalBrain.Neurons.On]
    public async Task Voice(InoVoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Show(InoDecisions.DecideVoice(State));
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
                NullLogger.Instance.LogSwallowed(error, "AppStudio build requires a live model and sandbox; falling back to local capture.");
            }
        }
        if (TryGetOptional<IMemory>("memory") is { } memory)
        {
            await memory.CaptureEpisode("ino.make", $"Target: {target} | Sentence: {sentence}", importance: 6);
        }
        await Show(InoDecisions.DecideMake(State, target, sentence, CurrentBrainId));
    }

    [DigitalBrain.Neurons.On]
    public async Task OnSettingsChanged(UserSettingsChanged? changed)
    {
        if (changed is null) { return; }
        var settings = await Get<IUserSettings>("settings").Read();
        _smart = IsSmart(settings);
        await EnsureProfileContextAsync();
        try { await EnsureConfiguredAsync(Get<IAgent>(AgentKeyOf(State.ConversationId)), settings); }
        catch (Exception error) when (error is not OperationCanceledException) { NullLogger.Instance.LogSwallowed(error, "The agent is optional when Ino runs without the AI module."); }
        if (!State.Status.StartsWith("Switched model to ", StringComparison.Ordinal))
        {
            await Show(State with { Status = $"Synced settings: {changed.DisplayName} ({changed.DefaultModel})" });
        }
    }

    public new InoAppState State => base.State;
    public View Render() => Render(State);
    public View RenderSide() => InoViewRenderer.RenderSide(State);
    protected override View Render(InoAppState state) => InoViewRenderer.Render(state, _transcript, _approvals, _smart);

    private string CurrentBrainId
    {
        get
        {
            try { return Id.Brain.Value is { Length: > 0 } b ? b : "local"; }
            catch (InvalidOperationException error)
            {
                NullLogger.Instance.LogSwallowed(error, "Unattached neuron uses default local brain id in isolated tests.");
                return "local";
            }
        }
    }

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            NullLogger.Instance.LogSwallowed(error, $"{typeof(T).Name} is optional when running in isolated unit tests.");
            return null;
        }
    }
}
