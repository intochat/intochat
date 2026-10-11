using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// T20-9: a grain belongs to a part when its state is in cluster storage; the machine-local grains never do.
public sealed class PartRoutesFacts
{
    [Fact(Timeout = 60_000)]
    public async Task NeuronsAreRoutedAndMachineLocalGrainsAreNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithPartLog().StartAsync(ct);
        var routes = ActivatorUtilities.CreateInstance<PartRoutes>(brain.SiloServices);

        Assert.True(routes.IsPartLogged(GrainType.Create("test.signal-counter")));
        Assert.True(routes.IsPartLogged(GrainType.Create("kernel.instances")));
        Assert.False(routes.IsPartLogged(GrainType.Create("vault")));
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCoresKernelGrainsBelongToTheCorePart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithPartLog().StartAsync(ct);
        var routes = ActivatorUtilities.CreateInstance<PartRoutes>(brain.SiloServices);
        var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

        Assert.Equal(PartId.Core(scope), routes.PartOf(GrainId.Create("kernel.instances", $"{scope}/test.signal-counter")));
        Assert.Equal(PartId.ForApp(scope, "counter"), routes.PartOf(GrainId.Create("test.signal-counter", $"{scope}/apps/counter/c1")));
        Assert.Null(routes.PartOf(GrainId.Create("vault", AccountSession.DefaultLogin)));
    }
}
