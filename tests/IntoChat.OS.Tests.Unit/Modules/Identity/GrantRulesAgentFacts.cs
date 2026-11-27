using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantRulesAgentFacts
{
    private static readonly string AssistantApp = CallerApps.Assistant;

    private static readonly Actor.Person TestUser = new("test-user", "test-account");

    private static Caller Agent() => IntoChat.OS.Tests.TestEdge.Authority.Agent(TestUser, new BrainId("test-brain"));

    private static Member MemberOf(Caller caller) => new()
    {
        PrincipalId = caller.For!.Principal,
        AccountId = caller.For.Account,
        BrainId = caller.Brain.Value,
        Role = MemberRole.Member,
        DisplayName = "Ino",
    };

    private static CallRequest Req(string target, params string[] types)
        => new() { TargetNeuron = target, Operation = "read", SemanticTypeIds = types };

    private static Grant AgentGrant(string target, string relation)
        => Grant.Tuple("brain:test-brain", "app:" + AssistantApp, target, relation);

    [Fact]
    public void AnAgentIsAnAgentCaller() => Assert.IsType<Actor.Agent>(Agent().Actor);

    [Fact]
    public void AnAgentWithoutAGrantIsDeniedMissingGrant()
    {
        var agent = Agent();
        var decision = GrantRules.Evaluate(agent, Req("test-brain/apps/files"), MemberOf(agent), [], PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public void AnAgentWithARunGrantOnTheTargetIsAllowed()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", GrantRelations.Run) };
        var decision = GrantRules.Evaluate(agent, Req("test-brain/apps/files"), MemberOf(agent), grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.True(decision!.Allowed);
    }

    [Fact]
    public void AnAgentReadOnlyCallWithNoSemanticTypesStillNeedsARunGrant()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", "view") };
        var decision = GrantRules.Evaluate(agent, Req("test-brain/apps/files"), MemberOf(agent), grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public void AnAgentOutsideTheWorkspaceIsDeniedOutsideWorkspace()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", GrantRelations.Run) };
        var decision = GrantRules.Evaluate(agent, Req("test-brain/apps/files"), null, grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.OutsideWorkspace, decision.Denial);
    }

    [Fact]
    public void AnAgentGetsNoOwnTargetAllowance()
    {
        var agent = Agent();
        var decision = GrantRules.Evaluate(agent, Req("test-brain/apps/assistant/state"), MemberOf(agent), [], PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public void AnAgentIsNotConnectionBound()
    {
        var agent = Agent();
        var request = Req("test-brain/apps/files");
        var denied = GrantRules.Evaluate(agent, request, MemberOf(agent), [], PrincipalSet.For(agent), connectionBound: true);
        Assert.False(denied!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, denied.Denial);
        var grants = new[] { AgentGrant("test-brain/apps/files", GrantRelations.Run) };
        Assert.True(GrantRules.Evaluate(agent, request, MemberOf(agent), grants, PrincipalSet.For(agent), connectionBound: true)!.Allowed);
    }

    [Fact]
    public void OnceToConsumeReturnsTheAgentsOnceGrant()
    {
        var agent = Agent();
        var once = new Grant { AppId = AssistantApp, SemanticTypeId = "contacts.read", Mode = GrantMode.Once, WorkspaceId = "test-brain" };
        var consumed = GrantRules.OnceToConsume(agent, Req("test-brain/apps/files", "contacts.read"), [once]);
        Assert.Equal(once, Assert.Single(consumed));
    }

    // The command-line host acts as the app "cli" for a person: it reads runs, sends app commands and starts workflows, and never reaches confirmations.
    [Theory]
    [InlineData("runs/test-brain/runs/index", true)]
    [InlineData("flows/test-brain/flows/index", true)]
    [InlineData("app.run/test-brain/commands/ticker/1", true)]
    [InlineData("workflow.run/test-brain/workflows/cli-1", true)]
    [InlineData("flow.code-orchestration/test-brain/code-orchestration/1", true)]
    [InlineData("runtime.confirmations/test-brain/confirmations", false)]
    [InlineData("files.vfs/test-brain/main", false)]
    [InlineData("runs/other-brain/runs/index", false)]
    public void TheCliReachesOnlyItsShippedGrants(string target, bool allowed)
    {
        var cli = IntoChat.OS.Tests.TestEdge.Authority.App(ShippedGrants.CliApp, TestUser, new BrainId("test-brain"));
        var grants = ShippedGrants.WithImplicit([], cli, "test-brain");
        var request = Req(target) with { TargetScope = target.Split('/')[1] };
        var decision = GrantRules.Evaluate(cli, request, MemberOf(cli), grants, PrincipalSet.For(cli), connectionBound: false);
        Assert.True(allowed == decision!.Allowed, decision.Explanation);
    }

    [Fact]
    public void StoredCliGrantsReplaceTheShippedOnes()
    {
        Grant[] stored = [Grant.Tuple("brain:test-brain", "app:" + ShippedGrants.CliApp, "runs/test-brain/runs/index", GrantRelations.Run)];
        Assert.Same(stored, ShippedGrants.WithImplicit(stored, IntoChat.OS.Tests.TestEdge.Authority.App(ShippedGrants.CliApp, TestUser, new BrainId("test-brain")), "test-brain"));
    }
}
