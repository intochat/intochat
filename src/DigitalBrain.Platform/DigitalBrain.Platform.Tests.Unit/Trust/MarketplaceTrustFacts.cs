using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

public sealed class MarketplaceTrustFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AMarketplaceWriteWhoseAccountIsNotThePublishersBrainOwnerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var machine = SequencerFacts.Machine();
        await TrustClientFacts.Register(brain.SiloServices.GetRequiredService<INodeDirectoryClient>(), machine, "mallory", []);
        await TrustClientFacts.Owned(brain, "alice", "home");
        var clients = GrainTrustClients.WithScratchCounter(brain.Grains, machine);

        // A brain under Mallory's account that nobody provisioned, Alice's brain named as Mallory's source, and no
        // source brain at all: none of them is a brain Mallory owns.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            clients.Upsert("mallory", new MarketplaceRecord("mallory", "mallory", "home", "echo", "Echo", "", "1.0.0", null)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            clients.Upsert("mallory", new MarketplaceRecord("alice", "alice", "home", "echo", "Echo", "", "1.0.0", null)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            clients.Upsert("mallory", new MarketplaceRecord("mallory", null, null, "echo", "Echo", "", "1.0.0", null)));
        Assert.Empty(await ((IMarketplaceClient)clients).List());
    }

    [Fact(Timeout = 60_000)]
    public async Task ARemovalChecksTheStoredListingsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        await brain.OwnBrainAsync("alice", "home");
        await brain.OwnBrainAsync("mallory", "mallory-home");
        var marketplace = brain.SiloServices.GetRequiredService<IMarketplaceClient>();
        await marketplace.Upsert("alice", new MarketplaceRecord("alice", "alice", "home", "echo", "Echo", "", "1.0.0", null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => marketplace.Remove("mallory", "alice", "echo"));
        Assert.NotNull(await marketplace.Read("alice", "echo"));
        await marketplace.Remove("alice", "alice", "echo");
        Assert.Null(await marketplace.Read("alice", "echo"));
    }
}
