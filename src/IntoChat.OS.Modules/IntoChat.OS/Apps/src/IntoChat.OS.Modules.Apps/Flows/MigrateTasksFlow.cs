using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Flows.Legacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps.Flows;

public sealed class MigrateTasksInner
{
    // The old task ids, in the directory's order: one step each.
    public string[] TaskIds { get; set; } = [];

    // The note flow each migrated task became.
    public MigratedTask[] Migrated { get; set; } = [];
}

public sealed class MigratedTask
{
    public string TaskId { get; set; } = "";

    public string NoteFlowId { get; set; } = "";
}

// Moves the deleted task engine's tasks onto note flows, once per brain (key "migrate-tasks"). Each task:{id} step reads the old
// record through its old neuron type, starts a NoteFlow keyed task-{id} with the title and description, maps the status (pending,
// running and awaiting confirmation stay open; completed arrives; failed, rolled back and cancelled cancel with the reason) and
// re-creates its parent and links as relations. A note already started is not started again, and ending or relating a note is
// idempotent, so a step that ran halfway runs again safely. Archive marks every old record Archived.
[DigitalBrain.Neuron(Alias)]
public sealed class MigrateTasksFlow : Flow<MigrateTasksInner>, IFlowStarter
{
    public const string Alias = "flow.migrate-tasks";

    public const string Key = "migrate-tasks";

    public const string ArchiveStep = "archive";

    private const string TaskStep = "task:";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override IReadOnlyList<string> Steps => [.. State.Inner.TaskIds.Select(id => TaskStep + id), ArchiveStep];

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<MigrateTasksInner>(innerJson, Json) ?? new MigrateTasksInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Steps], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    // The note flow key of an old task: flow keys hold no slash.
    public static string NoteKey(string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return "task-" + taskId.Replace('/', '-');
    }

    protected override async Task<StepResult<MigrateTasksInner>> Run(string step, MigrateTasksInner inner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(inner);
        if (step == ArchiveStep)
        {
            foreach (var id in inner.TaskIds) { await Context.Get<ILegacyTaskReader>(ILegacyTaskReader.Key(Id.Brain, id)).Archive(); }
            return new StepResult<MigrateTasksInner>(inner);
        }
        if (!step.StartsWith(TaskStep, StringComparison.Ordinal)) { throw new FlowFailed(step, $"Unknown step {step}."); }
        var taskId = step[TaskStep.Length..];
        var task = await Context.Get<ILegacyTaskReader>(ILegacyTaskReader.Key(Id.Brain, taskId)).Read();
        // No record behind a directory entry: nothing to move.
        if (task.TaskId.Length == 0) { return new StepResult<MigrateTasksInner>(inner); }
        // Called on the note itself, not through the index: the index calls flows, and this flow calling it is a cycle (as
        // PlanFlow's subtasks). The index folds the note from its FlowStarted.
        var note = new NeuronId(Id.Brain, NoteFlow.Alias, NoteKey(taskId));
        var noteId = note.ToString();
        var flow = Context.Get<INoteFlow>(note);
        if ((await flow.Head()).Status == FlowStatus.Pending)
        {
            var title = string.IsNullOrWhiteSpace(task.Title) ? "Task " + taskId : task.Title;
            await flow.StartFromJson(title, string.IsNullOrWhiteSpace(task.Owner) ? State.Owner : task.Owner, null,
                JsonSerializer.Serialize(new NoteInner { Text = task.Description }, NoteFlow.Json));
        }
        if (task.ParentId is { Length: > 0 } parent) { await flow.Relate(new FlowRelation(Target(inner, parent), RelationKind.Parent, null)); }
        foreach (var link in task.Links.Where(link => link.TargetId.Length > 0))
        {
            await flow.Relate(new FlowRelation(Target(inner, link.TargetId), KindOf(link.Kind), null));
        }
        switch (task.Status)
        {
            case LegacyTaskStatus.Completed:
                await flow.Resume(NoteFlow.Step, WaitOutcome.Arrived, State.Owner);
                break;
            case LegacyTaskStatus.Failed or LegacyTaskStatus.RolledBack or LegacyTaskStatus.Cancelled:
                await flow.Cancel(string.IsNullOrWhiteSpace(task.RollbackReason) ? "task " + task.Status.ToString().ToUpperInvariant() : task.RollbackReason);
                break;
        }
        if (!inner.Migrated.Any(item => item.TaskId == taskId))
        {
            inner.Migrated = [.. inner.Migrated, new MigratedTask { TaskId = taskId, NoteFlowId = noteId }];
        }
        return new StepResult<MigrateTasksInner>(inner);
    }

    // An old task id becomes its note; any other target is kept as it was.
    private string Target(MigrateTasksInner inner, string targetId)
        => inner.TaskIds.Contains(targetId) ? new NeuronId(Id.Brain, NoteFlow.Alias, NoteKey(targetId)).ToString() : targetId;

    // The old link kind is free text: a RelationKind name in any case (dashes and underscores ignored), anything else Relates.
    internal static RelationKind KindOf(string? kind)
    {
        var name = (kind ?? "").Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return name.Length > 0 && name.All(char.IsLetter) && Enum.TryParse<RelationKind>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : RelationKind.Relates;
    }
}

// The migration's start, kept off the flow neuron: it calls the index, which calls the flow.
internal static class MigrateTasks
{
    // Starts the migration when the old task directory lists any task; null when there is nothing to migrate. A second start is
    // refused by FlowIsNew (RuleViolationException): the migration runs once.
    internal static async Task<string?> StartOnce(ILegacyTaskDirectory directory, IFlows flows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(flows);
        var ids = (await directory.Read()).Entries.Select(entry => entry.TaskId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) { return null; }
        return await flows.Start(MigrateTasksFlow.Alias, "Migrate tasks", null, JsonSerializer.Serialize(new MigrateTasksInner { TaskIds = ids }, MigrateTasksFlow.Json), ct, MigrateTasksFlow.Key);
    }
}

// At silo start and on each brain's activation: starts flow.migrate-tasks for a brain with old tasks that has not started it.
internal sealed class MigrateTasksHost(IServiceProvider services, DigitalBrain.Spi.IClusterListeners listeners, ILogger<MigrateTasksHost> logger) : IStartupTask
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(MigrateTasksHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => EnsureAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });
        _ = Task.Run(() => EnsureAsync("owner"), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAsync(string brain)
    {
        RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { RuntimeCallerContext.Stamp(caller); }
            var id = new BrainId(brain);
            if ((await SiloNeurons.Get<IFlowStarter>(services, new NeuronId(id, MigrateTasksFlow.Alias, MigrateTasksFlow.Key)).Head()).Status != FlowStatus.Pending) { return; }
            await MigrateTasks.StartOnce(SiloNeurons.Get<ILegacyTaskDirectory>(services, ILegacyTaskDirectory.Key(id)),
                SiloNeurons.Get<IFlows>(services, IFlows.Key(brain)), CancellationToken.None);
        }
        catch (RuleViolationException refused) { logger.LogDebug(refused, "Old tasks in {Brain} are already being migrated.", brain); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Old tasks were not migrated in {Brain}.", brain); }
    }
}

internal sealed class MigrateTasksLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(MigrateTasksHost), ServiceLifecycleStage.Active,
            ct => ActivatorUtilities.CreateInstance<MigrateTasksHost>(services).Execute(ct), _ => Task.CompletedTask);
}
