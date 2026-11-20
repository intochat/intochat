using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// /ai/models lists the caller's brain when the edge stamped a valid one, and the deployment's list
// otherwise: an identified caller without a valid brain must not turn into a 500.
public sealed class ModelRouteKeyFacts
{
    private static Caller Caller(string brainId) => TestOS.Local().Edge.Person("person-1", "account-1", new BrainId(brainId));

    [Fact]
    public void A_valid_brain_lists_that_brain() => Assert.Equal("brain-1/main", AIModule.CatalogKey(Caller("brain-1")));

    // A caller without a valid brain ("", "a/b", "a:b") cannot be minted any more: BrainId validates, and the edge answers 401
    // for such a route (task 5 ruling 16), so the old theory has no caller to pass.

    [Fact]
    public void No_caller_gets_the_deployment_list() => Assert.Equal(IModelCatalog.DeploymentKey, AIModule.CatalogKey(null));
}
