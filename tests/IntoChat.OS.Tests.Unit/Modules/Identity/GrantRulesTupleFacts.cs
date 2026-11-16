using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantRulesTupleFacts
{
    private static CapabilityCaller App(string key) => CapabilityCallers.Of(AppPrincipal.Id(key), "test-user", "test-brain", CallerKind.App, key);
    private static CallRequest Req(string target, params string[] types) => new() { Caller = IntoChat.OS.Tests.TestEdge.From(App("photocraft")), TargetNeuron = target, Operation = "read", SemanticTypeIds = types };

    [Fact]
    public void OwnNamespaceIsAllowedWithoutGrant()
        => Assert.True(GrantRules.Evaluate(Req("test-brain/apps/photocraft/data"), null, [], PrincipalSet.For(App("photocraft")))!.Allowed);

    [Fact]
    public void TargetContainingAppNameAsInnerSegmentIsDenied()
    {
        var d = GrantRules.Evaluate(Req("test-brain/apps/x/shared/photocraft/data"), null, [], PrincipalSet.For(App("photocraft")));
        Assert.False(d!.Allowed); Assert.Equal(CallDenial.MissingGrant, d.Denial);
    }

    [Fact]
    public void UsersetGrantAllowsWhenPrincipalSetContainsIt()
    {
        var grant = Grant.Tuple("brain:test-brain", "contacts/friends#member", "test-brain/apps/gallery", GrantRelations.Run);
        var set = PrincipalSet.For(App("photocraft")).With("contacts/friends#member");
        Assert.True(GrantRules.Evaluate(Req("test-brain/apps/gallery"), null, [grant], set)!.Allowed);
        Assert.False(GrantRules.Evaluate(Req("test-brain/apps/gallery"), null, [grant], PrincipalSet.For(App("photocraft")))!.Allowed);
    }

    [Fact]
    public void WildcardTargetMatchesChildren()
    {
        var grant = Grant.Tuple("brain:test-brain", "app:photocraft", "test-brain/posts/*", "view");
        Assert.True(GrantRules.IsGranted("view", "test-brain/posts/42", [grant], PrincipalSet.For(App("photocraft"))));
        Assert.False(GrantRules.IsGranted("view", "test-brain/rooms/42", [grant], PrincipalSet.For(App("photocraft"))));
    }

    // A plain neuron's target is {type}/{brain}/{key}; the call filter names its brain, which owns the grant.
    [Fact]
    public void GrantOnAPlainNeuronMatchesThroughTheTargetBrain()
    {
        var grant = Grant.Tuple("brain:test-brain", "app:photocraft", "ui.workspace/test-brain/main", GrantRelations.Run);
        var request = Req("ui.workspace/test-brain/main") with { TargetScope = "test-brain" };
        Assert.True(GrantRules.Evaluate(request, null, [grant], PrincipalSet.For(App("photocraft")))!.Allowed);
        Assert.False(GrantRules.Evaluate(request with { TargetScope = "other-brain" }, null, [grant], PrincipalSet.For(App("photocraft")))!.Allowed);
    }

    [Fact]
    public void APlainNeuronTargetBelongsToTheBrainAfterItsType()
    {
        Assert.True(IntoChat.OS.Authority.Identity.Authority.DirectoryNeuron.InBrain("ui.workspace/test-brain/main", "test-brain"));
        Assert.True(IntoChat.OS.Authority.Identity.Authority.DirectoryNeuron.InBrain("test-brain/apps/gallery", "test-brain"));
        Assert.False(IntoChat.OS.Authority.Identity.Authority.DirectoryNeuron.InBrain("ui.workspace/other-brain/main", "test-brain"));
    }

    [Fact]
    public void GrantToOtherAppDoesNotLeakThroughFriendship()
    {
        var grant = Grant.Tuple("brain:test-brain", "app:ino", "test-brain/apps/files", GrantRelations.Run);
        Assert.False(GrantRules.Evaluate(Req("test-brain/apps/files"), null, [grant], PrincipalSet.For(App("photocraft")))!.Allowed);
    }
}
