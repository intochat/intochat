using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.AgentApp;

[Signal("agentapp.starter")]
public sealed record AgentAppStarterClicked(string Prompt) : DigitalBrain.Signal;

/// <summary>
/// Generic runtime host screen for pure-data agent apps (<c>Manifest.Agent != null</c>).
/// Mounts <see cref="ChatThreadNeuron"/> using <see cref="ConversationThreadProjection"/> and seats the app's
/// isolated <see cref="AgentDefinition"/> on its dedicated conversation.
/// </summary>
[Neuron("agentapp")]
public sealed partial class AgentAppScreen : AggregateScreen<AgentAppState>
{
    public const string ConversationSlotKey = "conversation";

    [Slot] private partial SlotCollection<IApp> Apps { get; }
    [Slot] private partial SlotCollection<IConversation> Conversations { get; }

    private readonly ILogger<AgentAppScreen>? _logger;
    private ConversationView? _conversationView;
    private long _seenRevision;
    private IReadOnlyList<ThreadMessage>? _syncedThread;
    private ChatThreadProps? _syncedProps;
    private AgentDefinition? _seatedDefinition;
    private AgentDefinition? _pendingDefinition;

    public AgentAppScreen() : this(null) { }

    [ActivatorUtilitiesConstructor]
    public AgentAppScreen(ILogger<AgentAppScreen>? logger = null)
        => _logger = logger;

    private ILogger Log => _logger ?? OsConsoleLog.For<AgentAppScreen>();

    private string AppName => Id.Key;

    private ConversationId TargetConversationId => AgentAppPolicy.ConversationOf(AppName);

    private string TargetConversationKey => IConversation.KeyOf(TargetConversationId);

    private NeuronId AppNeuronId
    {
        get
        {
            var fullKey = HostAppRef.Of(Id.Brain, AppName).Key;
            return new NeuronId(Id.Brain, IntoChat.OS.Host.Primitives.AppGrainType.Name, fullKey[(Id.Brain.Value.Length + 1)..]);
        }
    }

    private string AppFollowKey
    {
        get
        {
            var fullKey = HostAppRef.Of(Id.Brain, AppName).Key;
            return fullKey[(Id.Brain.Value.Length + 1)..];
        }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);

        await TryOptionalAsync("agentapp-thread-wires", async () =>
        {
            await FollowChild<ChatThreadNeuron, Submitted>(ConversationSlotKey);
            await FollowChild<ChatThreadNeuron, StopRequested>(ConversationSlotKey);
            await FollowChild<ChatThreadNeuron, RetryRequested>(ConversationSlotKey);
        });

        await TryOptionalAsync("agentapp-app-wire", () => Follow<IApp, AppChanged>(AppFollowKey));
        await TryOptionalAsync("agentapp-conversation-wire", () => Follow<IConversation, ConversationChanged>(TargetConversationKey));

        await LoadAppAndSeatAsync(refreshConversation: true);
    }

    protected override Node Render(AgentAppState state)
    {
        var shown = ProjectMessages(state);
        var props = ProjectProps(state, shown);

        var children = new List<Node>(3);
        var title = string.IsNullOrWhiteSpace(state.Title) ? AppName : state.Title;
        var headerChildren = new List<Node>(2) { Heading(title) };
        if (!string.IsNullOrWhiteSpace(state.Description))
        {
            headerChildren.Add(MutedText(state.Description));
        }
        children.Add(VStack(Space.Xs, [.. headerChildren]));

        if (state.Starters.Length > 0 && shown.Count == 0)
        {
            var starterButtons = state.Starters
                .Select((starter, index) => (Node)Button(starter)
                    .Key($"starter:{index}")
                    .Small()
                    .Sends(Sends(new AgentAppStarterClicked(starter))))
                .ToArray();
            children.Add(HStack(Space.Sm, starterButtons));
        }

        children.Add(Child<ChatThreadNeuron, ChatThreadProps>(ConversationSlotKey, props));
        return VStack(Space.Md, [.. children]);
    }

    [On]
    public Task OnThreadSubmitted(Submitted? submitted)
        => submitted is not null
            && SignalChild == ConversationSlotKey
            && submitted.Values.TryGetValue("text", out var text)
            ? SendPromptAsync(text)
            : Task.CompletedTask;

    [On]
    public Task OnStarterClicked(AgentAppStarterClicked? clicked)
        => clicked is not null ? SendPromptAsync(clicked.Prompt) : Task.CompletedTask;

    [On]
    public async Task OnThreadStop(StopRequested? requested)
    {
        if (requested is null || SignalChild != ConversationSlotKey)
        {
            return;
        }

        await TryOptionalAsync("agentapp-stop", () => Conversations[TargetConversationKey].Stop());
        await RefreshConversationAsync(full: false);
    }

    [On]
    public async Task OnThreadRetry(RetryRequested? requested)
    {
        if (requested is null || SignalChild != ConversationSlotKey)
        {
            return;
        }

        if (_conversationView is null)
        {
            await RefreshConversationAsync(full: true);
        }

        EntryId? targetReply = null;
        if (!string.IsNullOrWhiteSpace(requested.Id))
        {
            targetReply = new EntryId(requested.Id);
        }
        else if (_conversationView?.Runs.LastOrDefault(static r => !r.Posted) is { } latestRun)
        {
            targetReply = EntryIds.Reply(latestRun.RunId);
        }

        if (targetReply is not { } reply)
        {
            return;
        }

        try
        {
            await Conversations[TargetConversationKey].Regenerate(reply);
            await RefreshConversationAsync(full: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, $"agentapp retry failed for '{AppName}'");
            await CommitAsync(State with { Error = error.Message });
        }
    }

    [On]
    public async Task OnConversationChanged(ConversationChanged? changed)
    {
        if (changed is null || !string.Equals(changed.Id.Value, TargetConversationId.Value, StringComparison.Ordinal))
        {
            return;
        }

        await RefreshConversationAsync(full: _conversationView is null);

        if (_conversationView?.Active is null && State.PendingReseat && _pendingDefinition is { } pendingDef)
        {
            await TrySeatAsync(pendingDef);
        }
    }

    [On]
    public Task OnAppChanged(AppChanged? changed)
        => changed is not null && string.Equals(changed.Name, AppName, StringComparison.Ordinal)
            ? LoadAppAndSeatAsync(refreshConversation: false)
            : Task.CompletedTask;

    private async Task LoadAppAndSeatAsync(bool refreshConversation)
    {
        AppSnapshot? snapshot = null;
        try
        {
            snapshot = await Apps[AppNeuronId].Read();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, $"agentapp could not read app '{AppName}'");
        }

        var next = State;
        AgentDefinition? wanted = null;
        if (snapshot?.Manifest is { Agent: not null } manifest)
        {
            next = next with
            {
                Title = string.IsNullOrWhiteSpace(manifest.Title) ? AppName : manifest.Title,
                Description = manifest.Description ?? string.Empty,
                Starters = manifest.Agent.Starters ?? [],
            };
            wanted = AgentAppPolicy.Definition(manifest, snapshot.Connections);
        }
        else if (string.IsNullOrWhiteSpace(next.Title))
        {
            next = next with { Title = AppName };
        }

        if (refreshConversation)
        {
            await ReadConversationIntoCacheAsync(full: true);
            next = ApplyConversationToState(next, _conversationView);
        }

        if (wanted is not null && !AgentAppPolicy.SameSeat(_seatedDefinition, wanted))
        {
            if (_conversationView?.Active is null)
            {
                await TrySeatAsync(wanted, commit: false);
                next = next with { PendingReseat = false };
            }
            else
            {
                _pendingDefinition = wanted;
                next = next with { PendingReseat = true };
            }
        }

        await CommitAsync(next);
    }

    private async Task SendPromptAsync(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return;
        }

        var text = rawText.Trim();
        if (_seatedDefinition is null)
        {
            await LoadAppAndSeatAsync(refreshConversation: false);
        }

        var pendingId = Guid.NewGuid().ToString("N")[..12];
        await CommitAsync(State with { PendingId = pendingId, PendingText = text, Error = null });

        try
        {
            await TryOptionalAsync("agentapp-conversation-wire", () => Follow<IConversation, ConversationChanged>(TargetConversationKey));
            await Conversations[TargetConversationKey].Say(text);
            await RefreshConversationAsync(full: false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, $"agentapp Say failed for '{AppName}'");
            await CommitAsync(State with { PendingId = null, PendingText = null, Error = error.Message });
        }
    }

    private async Task TrySeatAsync(AgentDefinition definition, bool commit = true)
    {
        try
        {
            await Conversations[TargetConversationKey].Seat(AgentAppPolicy.AgentIdOf(AppName), definition);
            _seatedDefinition = definition;
            _pendingDefinition = null;
            if (commit && State.PendingReseat)
            {
                await CommitAsync(State with { PendingReseat = false });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, $"agentapp Seat failed for '{AppName}'");
        }
    }

    private async Task RefreshConversationAsync(bool full)
    {
        var updated = await ReadConversationIntoCacheAsync(full);
        if (!updated && _conversationView is null)
        {
            return;
        }

        var next = ApplyConversationToState(State, _conversationView);
        await CommitAsync(next);
    }

    private async Task<bool> ReadConversationIntoCacheAsync(bool full)
    {
        ConversationView incoming;
        try
        {
            incoming = await Conversations[TargetConversationKey].Read(full || _conversationView is null ? null : _seenRevision);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, $"agentapp conversation read failed for '{AppName}'");
            return false;
        }

        if (!full && _conversationView is not null && incoming.Revision < _seenRevision)
        {
            return false;
        }

        _conversationView = ConversationThreadProjection.MergeView(_conversationView, incoming, full);
        _seenRevision = _conversationView.Revision;
        return true;
    }

    private static AgentAppState ApplyConversationToState(AgentAppState current, ConversationView? view)
    {
        if (view is null)
        {
            return current;
        }

        var activeRunId = view.Active?.RunId;
        var hasMatchingUserEntry = current.PendingText is not null
            && view.Entries.OfType<Said>().Any(s => string.Equals(s.Text, current.PendingText, StringComparison.Ordinal));
        var clearPending = activeRunId is not null || hasMatchingUserEntry || view.Runs.Length > 0;

        return current with
        {
            ActiveRunId = activeRunId,
            PendingId = clearPending ? null : current.PendingId,
            PendingText = clearPending ? null : current.PendingText,
        };
    }

    private async Task CommitAsync(AgentAppState next)
    {
        await Show(next);

        var thread = ProjectMessages(next);
        var viewKnown = _conversationView is not null;
        if (!viewKnown && thread.Count == 0)
        {
            return;
        }

        var props = ProjectProps(next, thread);
        if (_syncedThread is { } synced && synced.SequenceEqual(thread) && Equals(props, _syncedProps))
        {
            return;
        }

        try
        {
            var command = new ImportMessages(thread, Replace: viewKnown);
            await Send(new Command(
                ConversationSlotKey,
                "control.importMessages",
                DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(command, typeof(ImportMessages))));
            _syncedThread = thread;
            _syncedProps = props;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _syncedThread = null;
            _syncedProps = null;
            Log.LogSwallowed(error, $"agentapp failed to sync chat thread for '{AppName}'");
        }
    }

    private IReadOnlyList<ThreadMessage> ProjectMessages(AgentAppState state)
    {
        var source = _conversationView is not null
            ? ConversationThreadProjection.ToThreadMessages(_conversationView)
            : [];
        return ConversationThreadProjection.CapAndAppendPending(
            source,
            pendingId: state.PendingId,
            pendingText: state.PendingText);
    }

    private ChatThreadProps ProjectProps(AgentAppState state, IReadOnlyList<ThreadMessage> shown)
    {
        var title = string.IsNullOrWhiteSpace(state.Title) ? AppName : state.Title;
        return ConversationThreadProjection.ToThreadProps(
            _conversationView,
            new ConversationThreadPropsOptions
            {
                Placeholder = $"Message {title}...",
                EmptyText = string.IsNullOrWhiteSpace(state.Description) ? $"Start chatting with {title}." : state.Description,
                ConversationKey = TargetConversationKey,
                Working = state.ActiveRunId is not null,
                HasPending = state.PendingId is not null,
                Error = string.IsNullOrWhiteSpace(state.Error) ? null : new ChatThreadError(state.Error),
                ShownMessages = shown,
                AutomationName = $"{title} conversation",
            });
    }

    private async Task TryOptionalAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, operation);
        }
    }
}
