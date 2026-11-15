using System.Reflection;
using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.Apps.Flows.Rules;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// Who may cancel a flow that waits for a person, by caller as it reaches the flows index.
public sealed class PersonCancelsApprovalWaitFacts
{
    private const string FlowId = "local/flow.change/1";

    private static readonly BrainId Brain = new("local");

    private static readonly Actor.Person Owner = new("person", "person");

    private static readonly NeuronId Tasks = new(Brain, "app", "tasks");

    private static readonly Dictionary<string, Caller?> Callers = new(StringComparer.Ordinal)
    {
        ["person"] = Of(Owner),
        ["person through an app"] = Of(Owner, via: Tasks),
        ["kernel"] = Of(new Actor.Kernel()),
        ["agent"] = Of(new Actor.Agent(Owner), "ino/conversations/main"),
        ["agent through an app"] = Of(new Actor.Agent(Owner), "ino/conversations/main", Tasks),
        ["mcp agent"] = Of(new Actor.Agent(Owner), "mcp"),
        ["local app"] = Of(new Actor.App("local", Owner)),
        ["local app through an app"] = Of(new Actor.App("local", Owner), via: Tasks),
        ["person with a conversation"] = Of(Owner, "ino/conversations/main"),
        ["scheduler"] = Of(new Actor.Scheduler()),
        ["nobody"] = null,
    };

    // Only the kernel mints a caller; a hop through a neuron sets Via and Inside.
    private static Caller Of(Actor actor, string? conversation = null, NeuronId? via = null)
        => (Caller)typeof(Caller)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Actor), typeof(BrainId), typeof(string), typeof(NeuronId), typeof(bool)])!
            .Invoke([actor, Brain, conversation, via, via is not null]);

    [Theory]
    [InlineData("person", false)]
    [InlineData("person through an app", false)]
    [InlineData("kernel", false)]
    [InlineData("agent", true)]
    [InlineData("agent through an app", true)]
    [InlineData("mcp agent", true)]
    [InlineData("local app", true)]
    [InlineData("local app through an app", true)]
    [InlineData("person with a conversation", true)]
    [InlineData("scheduler", true)]
    [InlineData("nobody", true)]
    public void BrokenForACallerThatIsNotAPersonOrTheKernelWhileTheFlowWaitsForAPerson(string caller, bool refused)
    {
        var waiting = new PersonCancelsApprovalWait(FlowId, "approval-gate", Callers[caller]);

        Assert.Equal(refused, waiting.Broken);
        Assert.False((waiting with { WaitingStep = null }).Broken);
    }

    [Fact]
    public void TheMessageNamesTheFlowAndTheStep()
    {
        var rule = new PersonCancelsApprovalWait(FlowId, "approval-gate", Callers["agent"]);

        Assert.Equal("apps.flows-person-cancels-approval-wait", rule.Name);
        Assert.Equal("flow local/flow.change/1 waits for a person at approval-gate: only a person cancels it", rule.Message);
    }
}
