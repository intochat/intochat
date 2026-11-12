using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority;

// The grant tuple model as the kernel must evaluate it: one relation for invocation, grants owned by the target
// brain, legacy rows confined to their workspace, and connection use only through a use-via grant.
[Collection("EdgeGrantsCache")]
public sealed class GrantModelFacts
{
    private static CapabilityCaller App(string key, string brain = "test-brain")
        => CapabilityCallers.Of(AppPrincipal.Id(key), "test-user", brain, CallerKind.App, key);

    private static CallRequest Req(CapabilityCaller caller, string target, params string[] types)
        => new() { Caller = caller.ToCaller(), TargetNeuron = target, Operation = "read", SemanticTypeIds = types };

    [Fact]
    public void RunGrantAllowsInvocationWithoutSemanticTypes()
    {
        var grant = Grant.Tuple("brain:test-brain", "app:photocraft", "test-brain/apps/gallery", GrantRelations.Run);
        var decision = GrantRules.Evaluate(Req(App("photocraft"), "test-brain/apps/gallery"), null, [grant], PrincipalSet.For(App("photocraft")));
        Assert.True(decision!.Allowed);
    }

    [Fact]
    public void LegacyRowWithoutTargetIsConfinedToItsWorkspace()
    {
        var legacy = new Grant { AppId = "calc", SemanticTypeId = "person.birthDate", WorkspaceId = "x" };

        Assert.Equal("x/*", Grant.Normalize(legacy).Target);
        Assert.True(GrantRules.IsGranted("person.birthDate", "x/person/me", [legacy], PrincipalSet.For(App("calc", "x"))));
        Assert.False(GrantRules.IsGranted("person.birthDate", "y/person/me", [legacy], PrincipalSet.For(App("calc", "y"))));
    }

    [Fact]
    public void GrantOwnedByAnotherBrainDoesNotReachTheTargetBrain()
    {
        var foreign = Grant.Tuple("brain:y", "app:y/apps/foo", "x/apps/secret", GrantRelations.Run);
        var caller = App("y/apps/foo", "y");
        Assert.False(GrantRules.IsGranted(GrantRelations.Run, "x/apps/secret", [foreign], PrincipalSet.For(caller)));

        var owned = Grant.Tuple("brain:x", "app:y/apps/foo", "x/apps/secret", GrantRelations.Run);
        Assert.True(GrantRules.IsGranted(GrantRelations.Run, "x/apps/secret", [owned], PrincipalSet.For(caller)));
    }

    [Fact]
    public void AuthorityEvaluatesTheTargetBrainNotTheCallerBrain()
    {
        var caller = App("y/apps/foo", "y");
        Assert.Equal("x", BrainAuthorityDecisions.GrantsBrainOf(Req(caller, "x/apps/secret")));
        Assert.Equal("y", BrainAuthorityDecisions.GrantsBrainOf(Req(caller, "")));
        Assert.Equal("y", BrainAuthorityDecisions.GrantsBrainOf(Req(caller, "neuron://y/person/me")));
    }

    [Fact]
    public async Task StarScopeDoesNotStandInForUseVia()
    {
        var cache = new EdgeGrantsCache();
        var connection = "x/connection/github/main";
        var caller = App("x/apps/foo", "x");

        cache.AddGrant("x", Grant.Tuple("brain:x", "app:x/apps/foo", connection, "*"));
        Assert.False(await cache.IsConnectionBoundAsync("x", caller, connection, TestContext.Current.CancellationToken));

        cache.AddGrant("x", Grant.Tuple("brain:x", "app:x/apps/foo", connection, "use-via:x/apps/foo"));
        Assert.True(await cache.IsConnectionBoundAsync("x", caller, connection, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FriendRegistrationNoLongerExists()
    {
        Assert.Null(typeof(EdgeGrantsCache).GetMethod("RegisterFriend"));
        Assert.Null(typeof(EdgeGrantsCache).GetMethod("RevokeFriend"));
        Assert.Null(typeof(EdgeGrantsCache).GetMethod("IsFriend"));
        Assert.Null(typeof(EdgeGrantsCache).GetMethod("OwnersSharingWithAsync"));
    }
}
