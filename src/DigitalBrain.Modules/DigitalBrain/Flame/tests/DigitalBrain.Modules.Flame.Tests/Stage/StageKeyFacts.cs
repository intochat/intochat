using DigitalBrain.Flame;
using DigitalBrain.Flame.Stage;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Stage;

public sealed class StageKeyFacts
{
    [Fact]
    public void TheOwnerAliasIsTheOneShapeASynapseCanBuild()
        => Assert.Equal("owner/stage/owner", StageKey.ForOwner("owner"));

    [Fact]
    public async Task TheResolverGivesTheOwnerTheAliasAndAnotherPersonTheDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var grains = brain.Services.GetRequiredService<IGrainFactory>();
        var owner = brain.Owner;
        var ownerCaller = new CallerContext { PrincipalId = owner.PrincipalId, AccountId = owner.AccountId, BrainId = owner.BrainId, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };
        var other = new CallerContext { PrincipalId = "carol", AccountId = "carol", BrainId = owner.BrainId, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

        Assert.Equal(StageKey.ForOwner(owner.BrainId), await StageNeuron.KeyFor(grains, ownerCaller));
        Assert.NotEqual(StageKey.ForOwner(owner.BrainId), await StageNeuron.KeyFor(grains, other));
        Assert.StartsWith(owner.BrainId + "/stage/", await StageNeuron.KeyFor(grains, other));
    }
}
