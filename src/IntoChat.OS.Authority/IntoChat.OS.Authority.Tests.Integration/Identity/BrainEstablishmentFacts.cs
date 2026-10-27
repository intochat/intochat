using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class BrainEstablishmentFacts(AuthorityHost host)
{
    [Fact]
    public async Task RegisteringAMemberCreatesTheirBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var aliceId = AuthorityHost.Unique("alice");
        var directory = brain.Platform.Directory();
        var member = await directory.RegisterAsync(aliceId, "correct-password", "Alice", ct);
        var state = await brain.Get<IBrain>(IBrain.Key(BrainScope.Create(member.AccountId, member.BrainId).Id)).Read();
        Assert.Equal(member.AccountId, state.Owner.AccountId);
    }

}
