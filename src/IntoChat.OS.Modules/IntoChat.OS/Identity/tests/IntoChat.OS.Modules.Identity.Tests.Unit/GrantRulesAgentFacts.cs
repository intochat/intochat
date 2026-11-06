using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantRulesAgentFacts
{
#pragma warning disable CS0618 // the only AppId that yields CallerKind.Agent; digitalbrain follow-up to un-obsolete it
    private static readonly string AssistantApp = CapabilityCallers.AssistantApp;
#pragma warning restore CS0618

    private static CapabilityCaller Agent() => new("test-user", "test-account", "test-brain") { AppId = AssistantApp };

    private static Member MemberOf(CapabilityCaller caller) => new()
    {
        PrincipalId = caller.PrincipalId,
        AccountId = caller.AccountId,
        BrainId = caller.BrainId,
        Role = MemberRole.Member,
        DisplayName = "Ino",
    };

    private static CallRequest Req(CapabilityCaller caller, string target, params string[] types)
        => new() { Caller = caller, TargetNeuron = target, Operation = "read", SemanticTypeIds = types };

    private static Grant AgentGrant(string target, string relation)
        => Grant.Tuple("brain:test-brain", "app:" + AssistantApp, target, relation);

    [Fact]
    public void AnAgentIsAnAgentCaller() => Assert.Equal(CallerKind.Agent, Agent().Kind);

    [Fact]
    public void AnAgentWithoutAGrantIsDeniedMissingGrant()
    {
        var agent = Agent();
        var decision = GrantRules.Evaluate(Req(agent, "test-brain/apps/files"), MemberOf(agent), [], PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public void AnAgentWithARunGrantOnTheTargetIsAllowed()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", GrantRelations.Run) };
        var decision = GrantRules.Evaluate(Req(agent, "test-brain/apps/files"), MemberOf(agent), grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.True(decision!.Allowed);
    }

    [Fact]
    public void AnAgentReadOnlyCallWithNoSemanticTypesStillNeedsARunGrant()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", "view") };
        var decision = GrantRules.Evaluate(Req(agent, "test-brain/apps/files"), MemberOf(agent), grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }

    [Fact]
    public void AnAgentOutsideTheWorkspaceIsDeniedOutsideWorkspace()
    {
        var agent = Agent();
        var grants = new[] { AgentGrant("test-brain/apps/files", GrantRelations.Run) };
        var decision = GrantRules.Evaluate(Req(agent, "test-brain/apps/files"), null, grants, PrincipalSet.For(agent), connectionBound: false);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.OutsideWorkspace, decision.Denial);
    }
}
