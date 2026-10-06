using DigitalBrain;
using DigitalBrain.Assistant;
using DigitalBrain.Compute.Usage;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AssistantRouteFacts
{
    [Fact]
    public void ComputeUsageIsServedUnderTheBrainRouteAndTheBuiltInWindowIsGone()
    {
        var snapshot = RouteSnapshot.Map(services =>
        {
            services.AddSingleton(typeof(IDigitalBrain), _ => null!);
            services.AddSingleton(typeof(IUsageStore), _ => null!);
        }, app => new AssistantModule().Configure(app));

        snapshot.AssertEveryMethodAndRouteIsDistinct();
        var routes = snapshot.Routes;
        Assert.Contains("/brains/{brainId}/compute/usage", routes);
        Assert.DoesNotContain("/brains/{brainId}/applications/assistant/open", routes);
        Assert.DoesNotContain("/brains/{brainId}/applications/assistant/start", routes);
        Assert.DoesNotContain("/brains/{brainId}/built-in/activate", routes);
        Assert.DoesNotContain("/brains/{brainId}/built-in/assistant/open", routes);
    }
}
