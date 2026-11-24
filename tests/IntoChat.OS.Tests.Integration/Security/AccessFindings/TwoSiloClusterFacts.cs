using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Two real OS silos in one cluster (loopback, ephemeral ports, this process only): the second host joins the first one's
// membership the way the Azure install's second container app (the Cloud app, or a new revision of the OS app while the old
// one still runs) joins the shared cluster.
[Trait("Category", "AccessFindings")]
public sealed class TwoSiloClusterFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The reproduction of the single-silo assumption. The first silo's startup activates the identity grains; the second
    // silo's own startup (a module caller, the integration seeder) calls them on the first silo, which refuses the call
    // because the origin mark of the second process does not count there.
    [Fact]
    public async Task ASecondSilo_CannotStart_BecauseItsInternalCallsToTheFirstAreRefused()
    {
        await using var a = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var primary = a.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Endpoint;

        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var b = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder => Join(builder, primary));
        });

        Assert.Contains(Chain(error), e => e is UnauthorizedAccessException && e.Message.Contains("cannot claim actor kind", StringComparison.Ordinal));
    }

    internal static void Join(WebApplicationBuilder builder, IPEndPoint primary)
    {
        var siloPort = int.Parse(builder.Configuration[DigitalBrain.Runtime.Orleans.TopologyContext.SiloPortKey]!, System.Globalization.CultureInfo.InvariantCulture);
        var gatewayPort = int.Parse(builder.Configuration[DigitalBrain.Runtime.Orleans.TopologyContext.GatewayPortKey]!, System.Globalization.CultureInfo.InvariantCulture);
        builder.UseOrleans(orleans => orleans.UseLocalhostClustering(siloPort, gatewayPort, primary));
    }

    internal static IEnumerable<Exception> Chain(Exception error)
    {
        var pending = new Stack<Exception>([error]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (current is AggregateException aggregate) { foreach (var inner in aggregate.InnerExceptions) { pending.Push(inner); } }
            else if (current.InnerException is { } inner) { pending.Push(inner); }
        }
    }
}
