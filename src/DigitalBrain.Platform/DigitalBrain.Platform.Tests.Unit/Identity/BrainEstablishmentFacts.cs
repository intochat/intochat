using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Testing.Module;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class BrainEstablishmentFacts
{
    [Fact]
    public async Task RegisteringAMemberCreatesTheirBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var directory = brain.Get<IIdentityDirectory>(IdentityGrains.Directory);
        var member = await directory.RegisterAsync("alice", "correct-password", "Alice", ct);
        var state = await brain.Get<IKernel>(BrainScope.Create(member.AccountId, member.BrainId).Id).Read();
        Assert.Equal(member.AccountId, state.Owner.AccountId);
    }

}
