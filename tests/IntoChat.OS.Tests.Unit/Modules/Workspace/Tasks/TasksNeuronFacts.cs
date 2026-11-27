using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Tasks;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Tasks;

public sealed class TasksNeuronFacts
{
    private static readonly BrainId Brain = new("local");

    [Fact]
    public async Task TasksNeuron_FullLifecycle_EmitsReactiveSignalsAndEnforcesPersonOnlyAnswerAsk()
    {
        await using var os = TestOS.Local();
        os.Register<TasksNeuron>();

        var tasks = os.Get<ITasks>(ITasks.Key(Brain.Value));
        var ct = TestContext.Current.CancellationToken;

        // 1. Create -> Queued
        TaskRecord created;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            created = await tasks.Create(
                title: "Ship Wave 1A",
                description: "Wire durable ITasks grain",
                tag: "Architecture",
                conversationId: "conversations/main",
                ct: ct);
            Assert.Equal(TaskStatus.Queued, created.Status);
            Assert.Equal("owner", created.Owner);

            // 2. Queued -> InProgress
            var inProgress = await tasks.Transition(created.TaskId, TaskStatus.InProgress, "Worker started", "local/flow.task-worker/w1", ct);
            Assert.Equal(TaskStatus.InProgress, inProgress.Status);
            Assert.Equal("local/flow.task-worker/w1", inProgress.FlowId);

            // 3. InProgress -> NeedsYou with multi-option TaskAskPrompt
            var prompt = new TaskAskPrompt("Which rollout track?", ["Canary", "Full"], RecommendedIndex: 0);
            var needsYou = await tasks.Ask(created.TaskId, prompt, confirmationPlanId: "plan-ask-1", ct: ct);
            Assert.Equal(TaskStatus.NeedsYou, needsYou.Status);
            Assert.NotNull(needsYou.PendingAsk);
            Assert.Equal(prompt.Question, needsYou.PendingAsk!.Question);
            Assert.Equal(["Canary", "Full"], needsYou.PendingAsk.Options);
            Assert.Equal(0, needsYou.PendingAsk.RecommendedIndex);
        }

        // 4. Non-person caller (Agent) CANNOT answer a task ask (RequiresPerson = true)
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(created.TaskId, 0, ct: ct));
        }

        // 5. Direct Person answers option 1 -> transitions back to InProgress and records LastAnswer
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var answered = await tasks.AnswerAsk(created.TaskId, 1, note: "Proceed with full rollout", ct: ct);
            Assert.Equal(TaskStatus.InProgress, answered.Status);
            Assert.Null(answered.PendingAsk);
            Assert.Equal(1, answered.LastAnswer?.SelectedOptionIndex);
            Assert.Equal("Full", answered.LastAnswer?.SelectedOption);
            Assert.Equal("Proceed with full rollout", answered.LastAnswer?.Note);

            // 6. Link artifact and Complete
            var linked = await tasks.LinkArtifact(created.TaskId, new TaskArtifactLink("pr", "PR #550", "https://github.com/intochat/intochat/pull/550"), ct);
            Assert.Single(linked.Artifacts);

            var completed = await tasks.Transition(created.TaskId, TaskStatus.Completed, "Merged cleanly", ct: ct);
            Assert.Equal(TaskStatus.Completed, completed.Status);
        }

        // 7. Verify reactive TaskChangedSignal and TaskAskedSignal were journaled
        var signals = os.Journal(Brain).Signals(ITasks.Key(Brain.Value)).Select(e => e.Signal).ToArray();
        Assert.Contains(signals, s => s is TaskAskedSignal asked && asked.TaskId == created.TaskId && asked.ConfirmationPlanId == "plan-ask-1");
        Assert.Contains(signals, s => s is TaskChangedSignal changed && changed.TaskId == created.TaskId && changed.Status == TaskStatus.Completed);
    }

    [Fact]
    public async Task TasksNeuron_ListsBeyond24ItemsWithoutCap()
    {
        await using var os = TestOS.Local();
        os.Register<TasksNeuron>();
        var tasks = os.Get<ITasks>(ITasks.Key(Brain.Value));
        var ct = TestContext.Current.CancellationToken;

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            for (var i = 1; i <= 30; i++)
            {
                await tasks.Create($"Task {i}", ct: ct);
            }

            var all = await tasks.List(ct: ct);
            Assert.Equal(30, all.Length);
        }
    }
}
