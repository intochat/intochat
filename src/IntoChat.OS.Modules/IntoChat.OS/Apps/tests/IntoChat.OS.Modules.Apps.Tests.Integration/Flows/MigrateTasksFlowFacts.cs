using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Flows.Legacy;
using IntoChat.OS.Modules.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class MigrateTasksFlowFacts
{
    private static readonly BrainId Brain = new("facts");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OldTasksBecomeNoteFlowsWithMappedStatusAndRelations()
    {
        await using var os = Os();
        SeedTasks(os,
            Old("a", "Ship it", LegacyTaskStatus.Completed, links: [new() { TargetId = "b", Kind = "blocked-by" }]),
            Old("b", "Broken", LegacyTaskStatus.Failed, parent: "c", reason: "the build broke"),
            Old("c", "Parent", LegacyTaskStatus.Running, links: [new() { TargetId = "elsewhere", Kind = "whatever" }]),
            Old("d", "Ask first", LegacyTaskStatus.AwaitingConfirmation));
        var flows = Flows(os);

        var migration = await MigrateTasks.StartOnce(os.Get<ILegacyTaskDirectory>(ILegacyTaskDirectory.Key(Brain)), flows, Ct);

        Assert.Equal($"{Brain.Value}/{MigrateTasksFlow.Alias}/{MigrateTasksFlow.Key}", migration);
        var done = await Eventually(os, () => flows.Read(migration!, Ct), view => view.Status == FlowStatus.Completed);
        Assert.Equal(["task:a", "task:b", "task:c", "task:d", MigrateTasksFlow.ArchiveStep], done.Done);

        var a = await Note(os, flows, "a", FlowStatus.Completed);
        var b = await Note(os, flows, "b", FlowStatus.Cancelled);
        var c = await Note(os, flows, "c", FlowStatus.Waiting);
        var d = await Note(os, flows, "d", FlowStatus.Waiting);
        Assert.Equal("Ship it", a.Title);
        Assert.Equal(new FlowRelation(NoteId("b"), RelationKind.BlockedBy, null), Assert.Single(a.Relations));
        Assert.Equal(new FlowRelation(NoteId("c"), RelationKind.Parent, null), Assert.Single(b.Relations));
        Assert.Equal("the build broke", b.Detail);
        Assert.Equal(new FlowRelation("elsewhere", RelationKind.Relates, null), Assert.Single(c.Relations));
        Assert.Empty(d.Relations);
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            Assert.True((await os.Get<ILegacyTaskReader>(ILegacyTaskReader.Key(Brain, id)).Read()).Archived);
        }
    }

    [Fact]
    public async Task MigrationRunsOnce()
    {
        await using (var empty = Os())
        {
            Assert.Null(await MigrateTasks.StartOnce(empty.Get<ILegacyTaskDirectory>(ILegacyTaskDirectory.Key(Brain)), Flows(empty), Ct));
        }
        await using var os = Os();
        SeedTasks(os, Old("a", "One", LegacyTaskStatus.Pending), Old("b", "Two", LegacyTaskStatus.Completed));
        var flows = Flows(os);
        var directory = os.Get<ILegacyTaskDirectory>(ILegacyTaskDirectory.Key(Brain));

        var migration = await MigrateTasks.StartOnce(directory, flows, Ct);
        await Eventually(os, () => flows.Read(migration!, Ct), view => view.Status == FlowStatus.Completed);

        var refused = await Assert.ThrowsAsync<RuleViolationException>(() => MigrateTasks.StartOnce(directory, flows, Ct));
        Assert.Contains($"flow exists: {migration}", refused.Message, StringComparison.Ordinal);
        await os.Idle();
        var notes = await flows.List(new FlowQuery(Type: NoteFlow.Alias), Ct);
        Assert.Equal([NoteId("a"), NoteId("b")], notes.Select(note => note.FlowId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FilesViewReadsOperationsQuery()
    {
        FilesStartedOperation[] started = [new("op-running", "write"), new("op-trash", "Move to trash"), new("op-failed", "rename", Done: true), new("op-unknown", "mkdir")];
        var query = new Query(new Dictionary<string, OperationView>
        {
            ["op-running"] = View("op-running", 0.4, null),
            ["op-trash"] = View("op-trash", 1, new OperationResult.Succeeded()),
            ["op-failed"] = View("op-failed", 0.5, new OperationResult.Failed("conflict", "exists")),
        });

        var views = await FilesWorkspaceNeuron.ReadOperations(query, started, error => Assert.Fail(error.Message));
        var cards = FilesWorkspaceNeuron.OperationCards(started, views, index => Ui.Button("cancel-" + index, "Cancel", "cancel"));

        Assert.Equal(["op-running", "op-trash", "op-failed", "op-unknown"], query.Reads);
        Assert.Equal(["40 %", "Moved to trash", "Failed: conflict", "0 %"], cards.Select(card => card.Children[0].Text));
        Assert.Equal(["Working", "Moved to trash", "Failed: conflict", "Waiting or working"], cards.Select(card => card.Detail ?? card.Text));
        Assert.Equal([2, 1, 1, 2], cards.Select(card => card.Children.Length));
    }

    private static OperationView View(string id, double fraction, OperationResult? result)
        => new(new OperationId(id), "files.write", "facts", fraction, result, DateTimeOffset.UnixEpoch, null);

    private sealed class Query(IReadOnlyDictionary<string, OperationView> views) : DigitalBrain.Spi.IOperationsQuery
    {
        public List<string> Reads { get; } = [];

        public Task<IReadOnlyList<OperationView>> Running(CancellationToken ct) => throw new NotSupportedException("the view reads by id");

        public Task<OperationView?> Read(OperationId id, CancellationToken ct)
        {
            Reads.Add(id.Value);
            return Task.FromResult(views.TryGetValue(id.Value, out var view) ? view : null);
        }
    }

    private static TestOS Os()
    {
        var os = TestOS.Local();
        os.Register(typeof(Signal).Assembly);
        os.Register(typeof(FlowStarted).Assembly);
        os.Register(typeof(IFlows).Assembly);
        os.Register(typeof(AppsModule).Assembly);
        return os;
    }

    private static IFlows Flows(TestOS os) => os.Get<IFlows>(IFlows.Key(Brain.Value));

    private static string NoteId(string taskId) => $"{Brain.Value}/{NoteFlow.Alias}/{MigrateTasksFlow.NoteKey(taskId)}";

    private static LegacyTaskState Old(string id, string title, LegacyTaskStatus status, string? parent = null, string? reason = null, LegacyTaskLinkState[]? links = null)
        => new() { TaskId = id, Title = title, Description = title + " notes", Owner = "owner", Status = status, ParentId = parent, RollbackReason = reason, Links = links ?? [] };

    // TestOS offers no public seed of a neuron's state; its journal's internal Save is the store the neuron loads from (as in
    // DeployFlowFacts). The records are the old engine's: the old neuron types, keys and state shape.
    private static void SeedTasks(TestOS os, params LegacyTaskState[] tasks)
    {
        foreach (var task in tasks) { Seed(os, ILegacyTaskReader.Key(Brain, task.TaskId), task); }
        Seed(os, ILegacyTaskDirectory.Key(Brain), new LegacyTaskDirectoryState
        {
            Entries = [.. tasks.Select(task => new LegacyTaskDirectoryEntryState { TaskId = task.TaskId, Title = task.Title, Status = task.Status, ParentId = task.ParentId, Links = task.Links })],
        });
    }

    private static void Seed<T>(TestOS os, NeuronId id, T state)
        => typeof(TestJournal).GetMethod("Save", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(T)).Invoke(os.Journal(Brain), [id, state, Array.Empty<Signal>()]);

    private static async Task<FlowView> Note(TestOS os, IFlows flows, string taskId, FlowStatus status)
    {
        var note = await Eventually(os, () => flows.Read(NoteId(taskId), Ct), view => view.Status == status);
        Assert.Equal(NoteFlow.Alias, note.Type);
        return note;
    }

    private static async Task<FlowView> Eventually(TestOS os, Func<Task<FlowView?>> read, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 400; i++)
        {
            await os.Idle();
            view = await read();
            if (view is not null && done(view)) { return view; }
            await Task.Delay(25, Ct);
        }
        Assert.Fail($"The flow did not reach the expected view; last: {view}");
        return view!;
    }
}
