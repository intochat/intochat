using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

public sealed class SeedNodeFacts
{
    private static Task<ITestBrain> StartAsync(int seedNodes, CancellationToken ct) =>
        TestBrain.Create()
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", seedNodes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .StartAsync(ct);

    [Fact(Timeout = 60_000)]
    public async Task TheStartupRegistersSeedNodesWithOwnerPlatform()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(3, ct);

        var nodes = await brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List();

        Assert.Equal(3, nodes.Length);
        Assert.All(nodes, node => Assert.Equal("platform", node.OwnerAccount));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARestartDoesNotDuplicateSeedNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(3, ct);

        await ActivatorUtilities.CreateInstance<SeedNodeRegistration>(brain.SiloServices).Execute(ct);

        Assert.Equal(3, (await brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List()).Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task NoSeedNodesByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);

        Assert.Empty(await brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).List());
    }
}
