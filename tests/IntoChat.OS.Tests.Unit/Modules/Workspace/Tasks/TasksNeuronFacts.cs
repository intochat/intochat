using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
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

    public interface ITestInoAnswerAskRelay
    {
        Task<TaskRecord> RelayAnswerAsk(NeuronId tasksId, string taskId, string selectedOption);
    }

    [Neuron("app.ino")]
    private sealed class TestInoAnswerAskRelay : Neuron, ITestInoAnswerAskRelay
    {
        public Task<TaskRecord> RelayAnswerAsk(NeuronId tasksId, string taskId, string selectedOption)
            => Context.Get<ITasks>(tasksId).AnswerAsk(taskId, selectedOption);
    }

    [Fact]
    public async Task AnswerAsk_EnforcesViaInoShellRelayBoundary()
    {
        await using var os = TestOS.Local();
        os.Register<TasksNeuron>()
            .Register<TestInoAnswerAskRelay>();

        var tasksId = ITasks.Key(Brain);
        var tasks = os.Get<ITasks>(tasksId);
        var otherBrain = new BrainId("other-brain");
        var sameBrainIno = new NeuronId(Brain, "app.ino", "default");
        var sameBrainFriends = new NeuronId(Brain, "app.friends", "default");
        var crossBrainIno = new NeuronId(otherBrain, "app.ino", "default");

        TaskRecord taskForDirect;
        TaskRecord taskForRelayed;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            taskForDirect = await tasks.Create("Direct ask");
            _ = await tasks.Ask(taskForDirect.TaskId, new TaskAskPrompt("Pick track?", ["Canary", "Full"], 0));

            taskForRelayed = await tasks.Create("Relayed ask");
            _ = await tasks.Ask(taskForRelayed.TaskId, new TaskAskPrompt("Pick env?", ["Sandbox", "Staging"], 0));
        }

        // REJECT: Conversation != null even with Inside=true and Via=app.ino
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", Brain, via: sameBrainIno, conversation: "main", inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(taskForRelayed.TaskId, "Sandbox"));
        }

        // REJECT: Inside == false with Via=app.ino
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", Brain, via: sameBrainIno, conversation: null, inside: false)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(taskForRelayed.TaskId, "Sandbox"));
        }

        // REJECT: Via.Type != "app.ino" (e.g., app.friends)
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", Brain, via: sameBrainFriends, conversation: null, inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(taskForRelayed.TaskId, "Sandbox"));
        }

        // REJECT: Via.Brain != Id.Brain (cross-brain app.ino)
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", Brain, via: crossBrainIno, conversation: null, inside: true)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tasks.AnswerAsk(taskForRelayed.TaskId, "Sandbox"));
        }

        // REJECT: Actor.Agent relayed through a real same-brain app.ino hop (Inside=true, Via=app.ino)
        var relay = os.Get<ITestInoAnswerAskRelay>(sameBrainIno);
        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => relay.RelayAnswerAsk(tasksId, taskForRelayed.TaskId, "Sandbox"));
        }

        // ALLOW: Direct person (Inside=false, Via=null, Conversation=null)
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var directAnswered = await tasks.AnswerAsk(taskForDirect.TaskId, "Canary");
            Assert.Equal(TaskStatus.InProgress, directAnswered.Status);
            Assert.Equal("Canary", directAnswered.LastAnswer?.SelectedOption);
        }

        // ALLOW: Same-brain app.ino-relayed person (Actor.Person, Inside=true, Conversation=null, Via={Brain==Id.Brain, Type=="app.ino"})
        using (os.Edge.Enter(os.Edge.RelayedPerson("owner", "owner", Brain, via: sameBrainIno, conversation: null, inside: true)))
        {
            var relayedAnswered = await tasks.AnswerAsk(taskForRelayed.TaskId, "Sandbox");
            Assert.Equal(TaskStatus.InProgress, relayedAnswered.Status);
            Assert.Equal("Sandbox", relayedAnswered.LastAnswer?.SelectedOption);
        }
    }
}
