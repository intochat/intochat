using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

public sealed class RemovePrincipalMemberFacts(AuthorityHost host)
{
    private static Member Of(string principal, string account, string brain, MemberRole role)
        => new() { PrincipalId = principal, AccountId = account, BrainId = brain, DisplayName = principal, Role = role };

    private async Task<(IIdentityClient Identity, string Scope, string Owner, string Other, string Account, string Brain)> Seed()
    {
        var identity = host.Brain.Services.GetRequiredService<IIdentityClient>();
        var account = AuthorityHost.Unique("account");
        var brain = AuthorityHost.Unique("brain");
        var owner = AuthorityHost.Unique("owner");
        var other = AuthorityHost.Unique("other");
        var scope = BrainScope.Create(account, brain).Id;
        await identity.EnsureMember(Of(owner, account, brain, MemberRole.Owner));
        await identity.EnsureMember(Of(other, account, brain, MemberRole.Member));
        return (identity, scope, owner, other, account, brain);
    }

    [Fact]
    public async Task RemovePrincipalMember_NonOwnerMember_RevokesBrainAccess()
    {
        var s = await Seed();
        Assert.NotNull(await s.Identity.Membership(s.Scope, s.Other));
        await s.Identity.RemovePrincipalMember(s.Scope, s.Other);
        Assert.Null(await s.Identity.Membership(s.Scope, s.Other));
        // Revocation and the principal index update are both awaited, in order with the earlier join.
        var brains = await TestWait.UntilAsync(_ => s.Identity.BrainsOf(s.Other),
            entries => entries.All(entry => entry.Id != s.Brain && entry.Id != s.Scope),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(brains, entry => entry.Id == s.Brain || entry.Id == s.Scope);
    }

    [Fact]
    public async Task RemovePrincipalMember_Owner_Throws()
    {
        var s = await Seed();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Identity.RemovePrincipalMember(s.Scope, s.Owner));
        Assert.NotNull(await s.Identity.Membership(s.Scope, s.Owner));
    }

    [Fact]
    public async Task RemovePrincipalMember_NotAMember_IsNoOp()
    {
        var s = await Seed();
        await s.Identity.RemovePrincipalMember(s.Scope, AuthorityHost.Unique("stranger"));
        Assert.NotNull(await s.Identity.Membership(s.Scope, s.Owner));
        Assert.NotNull(await s.Identity.Membership(s.Scope, s.Other));
    }

    [Fact]
    public async Task RemovePrincipalMember_OtherMembersUnaffected()
    {
        var s = await Seed();
        var third = AuthorityHost.Unique("third");
        await s.Identity.EnsureMember(Of(third, s.Account, s.Brain, MemberRole.Member));
        await s.Identity.RemovePrincipalMember(s.Scope, s.Other);
        Assert.NotNull(await s.Identity.Membership(s.Scope, third));
        Assert.NotNull(await s.Identity.Membership(s.Scope, s.Owner));
    }
}
