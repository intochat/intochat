using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.Apps.Flows.Rules;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// Who may cancel a flow that waits for a person, by caller as it reaches the flows index.
public sealed class PersonCancelsApprovalWaitFacts
{
    private const string FlowId = "owner/flow.change/1";

    private static readonly BrainId Brain = new("owner");

    private static readonly Actor.Person Owner = new("person", "person");

    private static readonly NeuronId Tasks = new(Brain, "app", "tasks");

    private static EdgeAuthority Edge => TestEdge.Authority;

    private static readonly Dictionary<string, Caller?> Callers = new(StringComparer.Ordinal)
    {
        ["person"] = Edge.Person("person", "person", Brain),
        ["person through an app"] = Edge.RelayedPerson("person", "person", Brain, via: Tasks, inside: true),
        ["kernel"] = Edge.Kernel(Brain),
        ["agent"] = Edge.Agent(Owner, Brain, "ino/conversations/main"),
        ["relayed person with a conversation"] = Edge.RelayedPerson("person", "person", Brain, via: Tasks, conversation: "ino/conversations/main", inside: true),
        ["mcp agent"] = Edge.Agent(Owner, Brain, "mcp"),
        ["app"] = Edge.App("tasks", Owner, Brain),
        ["app through an app"] = Edge.OnBehalfOf(Owner, Brain, viaApp: "tasks", via: Tasks),
        ["person with a conversation"] = Edge.Person("person", "person", Brain, "ino/conversations/main"),
        ["scheduler"] = Edge.Scheduler(Brain),
        ["nobody"] = null,
    };

    [Theory]
    [InlineData("person", false)]
    [InlineData("person through an app", false)]
    [InlineData("kernel", false)]
    [InlineData("agent", true)]
    [InlineData("relayed person with a conversation", true)]
    [InlineData("mcp agent", true)]
    [InlineData("app", true)]
    [InlineData("app through an app", true)]
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
        Assert.Equal("flow owner/flow.change/1 waits for a person at approval-gate: only a person cancels it", rule.Message);
    }
}
