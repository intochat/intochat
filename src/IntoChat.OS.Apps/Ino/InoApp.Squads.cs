using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.UI.Controls;
using CardMoved = DigitalBrain.UI.Commands.CardMoved;
using SelectionChanged = DigitalBrain.UI.Commands.SelectionChanged;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Apps.Ino;

public sealed partial class InoApp
{
    private async Task ActivateSquadControlsAsync()
    {
        await TryOptionalAsync("squad-plan-kanban", async () =>
        {
            await FollowChild<IKanbanControl, CardMoved>(InoSquads.KanbanKey);
            await FollowChild<IKanbanControl, SelectionChanged>(InoSquads.KanbanKey);
        });

        foreach (var task in State.ActiveTasks)
        {
            await EnsureSquadCheckFollowedAsync(task.TaskId);
        }
    }

    private async Task EnsureSquadCheckFollowedAsync(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var checkKey = InoSquads.CheckKey(taskId);
        await TryOptionalAsync("squad-plan-checkbox", () => FollowChild<ICheckBoxControl, Toggled>(checkKey));
    }

    private async Task SyncSquadCheckBoxesAsync(InoTaskSummary[] tasks)
    {
        foreach (var task in tasks)
        {
            await EnsureSquadCheckFollowedAsync(task.TaskId);
            var isDone = string.Equals(InoSquads.ColumnIdForSlug(task.Status), "done", StringComparison.Ordinal);
            var checkKey = InoSquads.CheckKey(task.TaskId);
            await TryOptionalAsync("squad-plan-checkbox-sync", () =>
                Send(new Command(
                    checkKey,
                    "control.setChecked",
                    DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new SetChecked(isDone), typeof(SetChecked)))));
        }
    }

    private async Task<InoEngineerRunSummary[]> ReadSquadEngineerRunsAsync(InoTaskSummary[] tasks)
    {
        if (EngineerRuns is not { } engineerRuns)
        {
            return State.SquadEngineerRuns;
        }

        var flowIds = tasks
            .Where(static t => !string.IsNullOrWhiteSpace(t.FlowId))
            .Select(static t => t.FlowId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var results = new List<InoEngineerRunSummary>(Math.Max(1, flowIds.Length));
        if (flowIds.Length == 0)
        {
            var latest = await ReadOptionalAsync<EngineerRunSummary?>("engineer-run-inspect", null, async () => await engineerRuns.InspectEngineerRun(null));
            if (latest is { Ok: true, FlowId.Length: > 0 })
            {
                results.Add(InoSquads.FromEngineerRun(latest));
            }
            return [.. results];
        }

        foreach (var flowId in flowIds)
        {
            var summary = await ReadOptionalAsync<EngineerRunSummary?>("engineer-run-inspect", null, async () => await engineerRuns.InspectEngineerRun(flowId));
            if (summary is { Ok: true, FlowId.Length: > 0 })
            {
                results.Add(InoSquads.FromEngineerRun(summary));
            }
        }

        return [.. results];
    }

    [On]
    public async Task CreateSquad(InoCreateSquad request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = InoSquads.PrepareCreate(
            request.Title,
            request.AgentIds,
            request.LeadAgentId,
            request.ExternalAccounts);
        if (!validation.Succeeded || validation.Plan is not { } plan)
        {
            var message = validation.Error ?? "Could not create Squad Room.";
            await Commit(State with { SquadError = message, Status = message });
            await RecordRejectionAsync(new Rejected("new-squad", ["invalid_squad"], [message]));
            return;
        }

        var settings = await ReadOptionalAsync<UserSettingsState?>("settings-read", null, async () => await UserSettings.Read())
            ?? new UserSettingsState();
        var snapshot = await RefreshPickerAsync();
        var (_, effectiveModel, _) = await InoModelSetup.ResolveForSettingsAsync(Setup, settings, snapshot);

        var seatedDefinitions = new List<(string AgentId, AgentDefinition Definition)>(plan.SeatedAgentIds.Length);
        var seenResolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? resolvedLeadId = null;

        foreach (var token in plan.SeatedAgentIds)
        {
            var resolved = await ResolveSquadAgentAsync(token, settings, effectiveModel);
            if (resolved is null)
            {
                var error = $"Could not resolve seated specialist '{token}'. Create the specialist first or use '@ino'.";
                await Commit(State with { SquadError = error, Status = error });
                await RecordRejectionAsync(new Rejected("new-squad", ["unknown_specialist"], [error]));
                return;
            }

            if (seenResolvedIds.Add(resolved.Value.AgentId))
            {
                seatedDefinitions.Add(resolved.Value);
            }

            if (string.Equals(token, plan.LeadAgentId, StringComparison.OrdinalIgnoreCase))
            {
                resolvedLeadId = resolved.Value.AgentId;
            }
        }

        if (seatedDefinitions.Count < 2)
        {
            const string duplicateError = "A Squad Room requires at least 2 distinct seated agents (duplicate aliases resolved to the same agent).";
            await Commit(State with { SquadError = duplicateError, Status = duplicateError });
            await RecordRejectionAsync(new Rejected("new-squad", ["invalid_squad"], [duplicateError]));
            return;
        }

        resolvedLeadId ??= seatedDefinitions[0].AgentId;

        var conversationId = "squad-" + Guid.NewGuid().ToString("N")[..12];
        var conversationKey = InoModel.ConversationKeyOf(conversationId);
        var conversation = Conversations[conversationKey];

        await conversation.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad, plan.Title, resolvedLeadId);
        foreach (var (agentId, definition) in seatedDefinitions)
        {
            await conversation.Seat(agentId, definition);
        }

        await TryOptionalAsync("squad-session-register", () =>
            SessionList.Create(conversationId, plan.Title, ThreadTrustPosture.OwnerTrusted, ThreadKind.Squad));

        ResetConversationCache(conversationId);
        await TryOptionalAsync("conversation-synapses", () => Follow<IConversation, ConversationChanged>(conversationKey));
        var recents = await InoModelSetup.ReadRecentConversationsAsync(Setup, State.RecentConversations);
        var nextState = InoModel.SwitchConversation(State, conversationId, recents) with
        {
            Title = plan.Title,
            SquadError = null,
            ChattingAboutChoiceTaskId = null,
        };
        await Commit(nextState);
        await RefreshTasksAsync(commit: false);
        await RefreshConversationAsync(full: true);
    }

    private async Task<(string AgentId, AgentDefinition Definition)?> ResolveSquadAgentAsync(
        string token,
        UserSettingsState settings,
        string? effectiveModel)
    {
        if (string.Equals(token, InoSquads.DefaultLeadAgentId, StringComparison.OrdinalIgnoreCase))
        {
            var effectiveSettings = string.IsNullOrWhiteSpace(effectiveModel)
                ? settings
                : settings with { DefaultModel = effectiveModel.Trim() };
            var inoDef = InoAgentDefinition.For(effectiveSettings) with { Handle = "@ino" };
            return (InoSquads.DefaultLeadAgentId, inoDef);
        }

        // Resolve custom Rung-1 specialist by appId or @handle, preserving AgentAppPolicy, BotMemoryScope, and ToolSources.
        var candidateAppId = token.StartsWith(AgentAppPolicy.ConversationPrefix, StringComparison.OrdinalIgnoreCase)
            ? token[AgentAppPolicy.ConversationPrefix.Length..]
            : token;

        AppSnapshot? matchedSnapshot = null;
        string? matchedAppId = null;

        if (TryGetAppNeuron(candidateAppId) is { } directApp)
        {
            await TryOptionalAsync("squad-specialist-read", async () => matchedSnapshot = await directApp.Read());
            if (matchedSnapshot?.Manifest?.Agent is not null)
            {
                matchedAppId = candidateAppId;
            }
            else
            {
                matchedSnapshot = null;
            }
        }

        if (matchedSnapshot is null && InstalledApps is not null && CurrentBrain.Value is { Length: > 0 })
        {
            var tiles = await ReadOptionalAsync<AppTile[]>("installed-apps", [], InstalledApps.List);
            foreach (var tile in tiles)
            {
                if (!string.Equals(tile.Kind, "agent", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(tile.Id))
                {
                    continue;
                }

                AppSnapshot? appSnap = null;
                await TryOptionalAsync("squad-specialist-scan", async () => appSnap = await Apps[AppNeuronIdOf(tile.Id)].Read());
                if (appSnap?.Manifest?.Agent is { } agent
                    && (string.Equals(tile.Id, candidateAppId, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(agent.Handle)
                            && string.Equals(agent.Handle.TrimStart('@'), candidateAppId, StringComparison.OrdinalIgnoreCase))))
                {
                    matchedSnapshot = appSnap;
                    matchedAppId = tile.Id;
                    break;
                }
            }
        }

        if (matchedSnapshot?.Manifest is not { Agent: not null } manifest || string.IsNullOrWhiteSpace(matchedAppId))
        {
            return null;
        }

        var definition = AgentAppPolicy.Definition(manifest, matchedSnapshot.Connections, matchedAppId, CurrentBrain.Value);
        if (definition.Model is null && !string.IsNullOrWhiteSpace(effectiveModel))
        {
            definition = definition with { Model = new ModelSelection(effectiveModel.Trim()) };
        }

        var agentId = AgentAppPolicy.AgentIdOf(matchedAppId);
        return (agentId, definition);
    }

    [On]
    public async Task OpenSquadPlan(InoOpenSquadPlan request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RefreshTasksAsync(commit: false);
        var artifact = State.ActiveArtifact is { Kind: not InoDrawerArtifact.EngineerPrKind } existing
            ? existing
            : new InoDrawerArtifact(
                Id: $"squad-plan:{State.ConversationId}",
                Title: string.IsNullOrWhiteSpace(State.Title) ? "Squad Plan" : $"{State.Title} · Plan",
                Kind: "squad-plan",
                Status: "done");
        await Commit(State.OpenArtifactDrawer(artifact) with
        {
            DrawerTab = InoSquads.DrawerTabPlan,
            DrawerOpen = true,
        });
        await SyncSquadCheckBoxesAsync(State.ActiveTasks);
    }

    [On]
    public async Task AddSquadTask(InoAddSquadTask request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            await RecordRejectionAsync(new Rejected("squad-add-task", ["invalid_task_title"], ["Task title is required."]));
            return;
        }

        if (Tasks is not { } tasks)
        {
            return;
        }

        var created = await tasks.Create(
            title: request.Title.Trim(),
            description: request.Description?.Trim() ?? string.Empty,
            tag: string.IsNullOrWhiteSpace(request.Tag) ? "Squad" : request.Tag.Trim(),
            conversationId: State.ConversationId);

        await EnsureSquadCheckFollowedAsync(created.TaskId);
        await RefreshTasksAsync();
    }

    [On]
    public async Task MoveSquadTask(InoMoveSquadTask request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskId) || InoSquads.TryTaskStatusForColumn(request.ColumnId) is null)
        {
            await RecordRejectionAsync(new Rejected("squad-move-task", ["invalid_move"], ["Valid task id and Squad column (planned, in-progress, needs-you, waiting, done) are required."]));
            return;
        }

        var columnId = InoSquads.ColumnIdForSlug(request.ColumnId);
        var move = new MoveCard(request.TaskId.Trim(), columnId, Math.Max(0, request.Index));
        await Send(new Command(
            InoSquads.KanbanKey,
            "control.moveCard",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(move, typeof(MoveCard))));
    }

    [On]
    public async Task ToggleSquadTask(InoToggleSquadTask request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TaskId))
        {
            return;
        }

        var taskId = request.TaskId.Trim();
        await EnsureSquadCheckFollowedAsync(taskId);
        var currentDone = State.ActiveTasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal)) is { } existing
            && string.Equals(InoSquads.ColumnIdForSlug(existing.Status), "done", StringComparison.Ordinal);
        var targetDone = request.Checked ?? !currentDone;

        await Send(new Command(
            InoSquads.CheckKey(taskId),
            "control.setChecked",
            DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new SetChecked(targetDone), typeof(SetChecked))));
    }

    [On]
    public async Task ChatAboutChoice(InoChatAboutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Active)
        {
            await Commit(State with { ChattingAboutChoiceTaskId = null });
            return;
        }

        var targetTaskId = !string.IsNullOrWhiteSpace(request.TaskId)
            ? request.TaskId.Trim()
            : State.ActiveTasks.FirstOrDefault(static t =>
                string.Equals(t.Status, "needs-you", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(t.AskQuestion)
                && t.AskOptions is { Length: >= 2 })?.TaskId;

        if (string.IsNullOrWhiteSpace(targetTaskId))
        {
            return;
        }

        await Commit(State with { ChattingAboutChoiceTaskId = targetTaskId });
    }

    [On]
    public async Task OnSquadCardMoved(CardMoved? moved)
    {
        if (moved is null || SignalChild != InoSquads.KanbanKey)
        {
            return;
        }

        if (InoSquads.TryTaskStatusForColumn(moved.ColumnId) is not { } targetStatus)
        {
            return;
        }

        if (Tasks is { } tasks)
        {
            await TryOptionalAsync("squad-kanban-transition", () => tasks.Transition(moved.CardId, targetStatus));
            await RefreshTasksAsync();
        }
    }

    [On]
    public Task OnSquadCardSelected(SelectionChanged? changed)
        => changed is not null && SignalChild == InoSquads.KanbanKey
            ? Commit(State with { SelectedSquadTaskId = changed.Id })
            : Task.CompletedTask;

    [On]
    public async Task OnSquadCheckToggled(Toggled? toggled)
    {
        if (toggled is null || !InoSquads.TryTaskIdFromCheckKey(SignalChild, out var taskId))
        {
            return;
        }

        if (Tasks is { } tasks)
        {
            var existing = await ReadOptionalAsync<TaskRecord?>("squad-check-read", null, () => tasks.Read(taskId));
            if (existing is null)
            {
                return;
            }

            TaskStatus? desired = toggled.IsOn
                ? (existing.Status != TaskStatus.Completed ? TaskStatus.Completed : null)
                : (existing.Status == TaskStatus.Completed ? TaskStatus.InProgress : null);
            if (desired is { } nextStatus)
            {
                await TryOptionalAsync("squad-check-transition", () => tasks.Transition(taskId, nextStatus));
                await RefreshTasksAsync();
            }
        }
    }
}
