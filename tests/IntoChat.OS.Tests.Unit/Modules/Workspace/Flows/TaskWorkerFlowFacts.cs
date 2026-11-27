using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class TaskWorkerFlowFacts
{
    private static readonly BrainId Brain = new("local");

    public sealed record WorkerConvState(AgentDefinition? Seated, ConversationView? View)
    {
        public WorkerConvState() : this(null, null) { }
    }

    public interface IScriptedWorkerConversation : IConversation
    {
        Task<AgentDefinition?> SeatedDefinition();
    }

    [DigitalBrain.Neurons.Neuron(IConversation.Type)]
    public sealed class ScriptedWorkerConversation : DigitalBrain.Neurons.Neuron<WorkerConvState>, IScriptedWorkerConversation
    {
        public static string NextReply { get; set; } = """{"summary":"Done"}""";

        private ConversationId ConvId => ConversationId.Parse("main");

        public Task<AgentDefinition?> SeatedDefinition() => Task.FromResult(State.Seated);

        public Task Seat(string agentId, AgentDefinition definition)
            => Save(State with { Seated = definition }, new ConversationChanged(ConvId, 0));

        public Task Say(string text, ModelId? model = null)
        {
            var run = new AgentRun("run-1", AgentRunStatus.Completed, text, Context.Time.GetUtcNow())
            {
                Reply = NextReply,
            };
            var view = new ConversationView(ConvId, 1, [], [], null, [run], false, []);
            return Save(State with { View = view }, new ConversationChanged(ConvId, 1));
        }

        public Task<ConversationView> Read(long? sinceRevision = null)
            => Task.FromResult(State.View ?? new ConversationView(ConvId, 0, [], [], null, [], false, []));

        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => Task.CompletedTask;
        public Task Post(string text, string id, Visibility visibility = Visibility.Everyone) => Task.CompletedTask;
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;
        public Task Invite(AccountId friend) => Task.CompletedTask;
    }

    [Fact]
    public void ResolveContracts_EnforcesNonEmptyLeastPrivilegeSubset()
    {
        var defaults = TaskWorkerFlow.ResolveContracts(null);
        Assert.NotEmpty(defaults);
        Assert.Contains(typeof(ITasks).FullName!, defaults);
        Assert.Contains(typeof(IFiles).FullName!, defaults);
        Assert.Contains(typeof(IEngineerRuns).FullName!, defaults);

        // Disallowed/privileged contracts are stripped while allowed subset is preserved.
        var narrowed = TaskWorkerFlow.ResolveContracts([typeof(ITasks).FullName!, "IntoChat.OS.Modules.Apps.IUserSettings"]);
        Assert.Equal([typeof(ITasks).FullName!], narrowed);

        // If all requested contracts are disallowed, falls back to safe least-privilege defaults (never empty).
        var fallback = TaskWorkerFlow.ResolveContracts(["IntoChat.OS.Modules.Apps.IUserSettings"]);
        Assert.Equal(defaults, fallback);
    }

    [Fact]
    public async Task TaskWorkerFlow_EquipsWorkerWithContracts_PausesOnPersonOnlyAsk_AndCompletesTask()
    {
        ScriptedWorkerConversation.NextReply = """
            {
              "ask": {
                "question": "Which branch should we target?",
                "options": ["master", "release/v2"],
                "recommendedIndex": 0
              },
              "artifacts": [
                { "kind": "pr", "label": "Wave 1A PR", "uri": "https://github.com/intochat/intochat/pull/999", "commitSha": "abc1234" }
              ]
            }
            """;

        await using var os = TestOS.Local();
        os.Register<TasksNeuron>()
            .Register<TaskWorkerFlow>()
            .Register<ScriptedWorkerConversation>();

        var tasks = os.Get<ITasks>(ITasks.Key(Brain));
        TaskRecord task;
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            task = await tasks.Create("Run migration", "Execute with least-privilege tools");
        }

        var flowKey = "worker-1";
        var flowId = new NeuronId(Brain, TaskWorkerFlow.Alias, flowKey);
        var starter = os.Get<IFlowStarter>(flowId);
        var innerJson = JsonSerializer.Serialize(
            new TaskWorkerInner
            {
                TaskId = task.TaskId,
                Goal = "Run migration safely",
                Contracts = [typeof(ITasks).FullName!, typeof(IFiles).FullName!],
            },
            TaskWorkerFlow.Json);

        using (os.Edge.Enter(os.Edge.Agent(new Actor.Person("owner", "owner"), Brain)))
        {
            await starter.StartFromJson("Run migration", "owner", null, innerJson);
        }

        await os.Idle();

        // 1. Worker agent was seated with the requested least-privilege Contracts subset (NOT empty!).
        var workerConv = os.Get<IScriptedWorkerConversation>(new NeuronId(Brain, IConversation.Type, "flow-conv/" + flowKey));
        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var seated = await workerConv.SeatedDefinition();
            Assert.NotNull(seated);
            Assert.Equal([typeof(ITasks).FullName!, typeof(IFiles).FullName!], seated!.Contracts);

            // 2. Flow paused at "ask" with a person-only multi-option ConfirmationPlan, and linked ITasks transitioned to NeedsYou.
            var pausedHead = await starter.Head();
            Assert.Equal(FlowStatus.Waiting, pausedHead.Status);

            var pausedTask = await tasks.Read(task.TaskId);
            Assert.Equal(TaskStatus.NeedsYou, pausedTask?.Status);
            Assert.Equal("Which branch should we target?", pausedTask?.PendingAsk?.Question);
        }

        var waitingPlan = Assert.Single(os.Confirmations.Waiting);
        Assert.True(waitingPlan.Plan.RequiresPerson);
        Assert.Equal("Which branch should we target?", waitingPlan.Plan.Title);
        Assert.Equal(["master", "release/v2"], waitingPlan.Plan.Options);
        Assert.Equal(0, waitingPlan.Plan.RecommendedOptionIndex);

        // 3. Agent cannot spoof the person-only Ask answer.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            os.Confirmations.ResolveOption(waitingPlan.PlanId, 0, ConfirmationPrincipal.Agent("spoof")));

        // 4. Direct human Person chooses option 1 ("release/v2") -> flow resumes & completes, updating ITasks to Completed with artifact.
        await os.Confirmations.ChooseOption(waitingPlan.PlanId, 1);
        await os.Idle();

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Brain)))
        {
            var completedHead = await starter.Head();
            Assert.Equal(FlowStatus.Completed, completedHead.Status);

            var finalTask = await tasks.Read(task.TaskId);
            Assert.Equal(TaskStatus.Completed, finalTask?.Status);
            Assert.Contains("release/v2", finalTask?.Summary ?? "", StringComparison.Ordinal);
            Assert.Contains(finalTask!.Artifacts, a => a.Kind == "pr" && a.Uri == "https://github.com/intochat/intochat/pull/999" && a.CommitSha == "abc1234");
        }
    }
}
