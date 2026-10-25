using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// /ai/models lists the caller's brain when the edge stamped a valid one, and the deployment's list
// otherwise: an identified caller without a valid brain must not turn into a 500.
public sealed class ModelRouteKeyFacts
{
    private static CallerContext Caller(string brainId) => new()
    {
        PrincipalId = "person-1",
        AccountId = "account-1",
        BrainId = brainId,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    [Fact]
    public void A_valid_brain_lists_that_brain() => Assert.Equal("brain-1/default", AIModule.CatalogKey(Caller("brain-1")));

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void A_caller_without_a_valid_brain_gets_the_deployment_list(string brainId)
        => Assert.Equal("default", AIModule.CatalogKey(Caller(brainId)));

    [Fact]
    public void No_caller_gets_the_deployment_list() => Assert.Equal("default", AIModule.CatalogKey(null));
}
