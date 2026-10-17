using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class BrainEstablishmentFacts
{
    [Fact]
    public async Task RegisteringAMemberCreatesTheirBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = brain.Grains.GetGrain<IIdentityDirectory>(IdentityGrains.Directory);
        var member = await directory.RegisterAsync("alice", "correct-password", "Alice", ct);
        var state = await brain.Get<IBrain>(BrainScope.Create(member.AccountId, member.BrainId).Id).Read();
        Assert.Equal(member.AccountId, state.Owner.AccountId);
    }

}
