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

        var tasks = os.Get<ITasks>(ITasks.Key(Brain));

        // 1. Create -> Queued
        TaskRecord created;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            created = await tasks.Create(
                title: "Ship Wave 1A",
                description: "Wire durable ITasks grain",
                tag: "Architecture",
                flowId: "local/flow.task-worker/w1");
            Assert.Equal(TaskStatus.Queued, created.Status);
            Assert.Equal("owner", created.Owner);
            Assert.Equal("local/flow.task-worker/w1", created.FlowId);

            // 2. Queued -> InProgress
            var inProgress = await tasks.Transition(created.TaskId, TaskStatus.InProgress, "Worker started");
            Assert.Equal(TaskStatus.InProgress, inProgress.Status);

            // 3. InProgress -> NeedsYou with multi-option TaskAskPrompt
            var prompt = new TaskAskPrompt("Which rollout track?", ["Canary", "Full"], RecommendedIndex: 0);
            var needsYou = await tasks.Ask(created.TaskId, prompt);
            Assert.Equal(TaskStatus.NeedsYou, needsYou.Status);
            Assert.NotNull(needsYou.PendingAsk);
            Assert.Equal(prompt.Question, needsYou.PendingAsk!.Question);
            Assert.Equal(["Canary", "Full"], needsYou.PendingAsk.Options);
            Assert.Equal(0, needsYou.PendingAsk.RecommendedIndex);
        }

        // 4. Non-person caller (Agent) CANNOT answer a task ask OR bypass NeedsYou via Transition
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(created.TaskId, "Canary"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.Transition(created.TaskId, TaskStatus.InProgress, "spoofed bypass"));
        }

        // 5. Direct Person answers "Full" -> transitions back to InProgress and records LastAnswer
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var answered = await tasks.AnswerAsk(created.TaskId, "Full");
            Assert.Equal(TaskStatus.InProgress, answered.Status);
            Assert.Null(answered.PendingAsk);
            Assert.Equal(1, answered.LastAnswer?.SelectedOptionIndex);
            Assert.Equal("Full", answered.LastAnswer?.SelectedOption);

            // 6. Link artifact and Complete
            var linked = await tasks.LinkArtifact(
                created.TaskId,
                new TaskArtifactLink("pr", "PR #525", "https://github.com/intochat/intochat/pull/525", "37ec45c89"));
            Assert.Single(linked.Artifacts);
            Assert.Equal("37ec45c89", linked.Artifacts[0].CommitSha);

            var completed = await tasks.Transition(created.TaskId, TaskStatus.Completed, "Merged cleanly");
            Assert.Equal(TaskStatus.Completed, completed.Status);

            var readBack = await tasks.Read(created.TaskId);
            Assert.Equal(TaskStatus.Completed, readBack?.Status);
        }

        // 7. Verify reactive TasksChanged and TaskAsked signals were journaled
        var signals = os.Journal(Brain).Signals(ITasks.Key(Brain)).Select(e => e.Signal).ToArray();
        Assert.Contains(signals, s => s is TaskAsked asked && asked.TaskId == created.TaskId && asked.Question == "Which rollout track?");
        Assert.Contains(signals, s => s is TasksChanged changed && changed.TaskId == created.TaskId && changed.Status == TaskStatus.Completed);
    }

    [Fact]
    public async Task TasksNeuron_ListsBeyond24ItemsWithoutCap()
    {
        await using var os = TestOS.Local();
        os.Register<TasksNeuron>();
        var tasks = os.Get<ITasks>(ITasks.Key(Brain));

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            for (var i = 1; i <= 30; i++)
            {
                await tasks.Create($"Task {i}");
            }

            var all = await tasks.List();
            Assert.Equal(30, all.Length);
        }
    }
}
