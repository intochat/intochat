using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// A brain's owner is on record before the brain is born. The OS installs default apps from a
// creation handler, and their behaviors call back into the brain as an App caller at once; that
// caller must never be adopted as the owner, and the real owner must never be skipped.
public sealed class OwnerProvisioningFacts(AuthorityHost host)
{
    [Trait("host", "own")]
    [Fact]
    public async Task EveryPersonToRegisterOwnsTheirOwnBrainWhenACreationHandlerCallsInFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new AppCallerAtBirth();
        await using var brain = await TestBrain.Create()
            // The OS's own seam at brain birth (it installs default apps there): the handler stands in for it.
            .ConfigureServices(services => services.AddSingleton<ICreationHandler>(provider => handler.Using(provider.GetRequiredService<AuthorityNeurons>())))
            .StartAsync(ct);
        var directory = brain.Platform.Directory();
        foreach (var person in new[] { "alice", "bob", "carol" })
        {
            var member = await directory.RegisterAsync(person, "correct-password-123", person, ct);
            var authority = brain.Platform.BrainAuthority(BrainScope.Create(member.AccountId, member.BrainId).Id);
            var owner = await authority.Membership(person);
            Assert.NotNull(owner);
            Assert.Equal(MemberRole.Owner, owner.Role);
            Assert.Null(await authority.Membership("os"));
        }
        // The handler reached every brain's authority while the brain was being born: the three people's and the
        // owner brain the machine bears at boot (R59).
        Assert.Equal(4, handler.Decisions.Count);
    }

    [Fact]
    public async Task AnAppCallerReachingAnUnprovisionedAuthorityIsDeniedAndNotCrowned()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var brainId = AuthorityHost.Unique("brain");
        var accountId = AuthorityHost.Unique("account");
        var aliceId = AuthorityHost.Unique("alice");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(accountId, brainId).Id);
        var decision = await authority.Authorize(AppCallerAtBirth.Request(accountId, brainId));
        Assert.NotNull(decision);
        Assert.False(decision.Allowed);
        Assert.Null(await authority.Membership("os"));
        await authority.ProvisionOwner(new Member { PrincipalId = aliceId, AccountId = accountId, BrainId = brainId, DisplayName = "Alice", Role = MemberRole.Owner });
        Assert.Equal(MemberRole.Owner, (await authority.Membership(aliceId))!.Role);
    }

    // The development brain: born lazily under the synthetic owner's account, so its first caller,
    // even an app, finds the synthetic owner on record rather than becoming the owner.
    [Fact]
    public async Task AnAppCallerInTheSyntheticOwnersBrainFindsTheSyntheticOwnerOnRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(AccountSession.DefaultLogin, "dev").Id);
        // R68: the machine's grant policy provisions the synthetic owner through the signed path; Authorize never does.
        var decision = await brain.Services.GetRequiredService<IntoChat.OS.Authority.Identity.Grants.IGrantPolicySource>()
            .AuthorizeAsync(AppCallerAtBirth.Request(AccountSession.DefaultLogin, "dev"), ct);
        Assert.True(decision is null || decision.Allowed);
        Assert.Null(await authority.Membership("os"));
        Assert.Equal(MemberRole.Owner, (await authority.Membership(AccountSession.DefaultLogin))!.Role);
    }

    [Fact]
    public async Task ASecondOwnerIsRefusedLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var brainId = AuthorityHost.Unique("brain");
        var accountId = AuthorityHost.Unique("account");
        var aliceId = AuthorityHost.Unique("alice");
        var malloryId = AuthorityHost.Unique("mallory");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(accountId, brainId).Id);
        var alice = new Member { PrincipalId = aliceId, AccountId = accountId, BrainId = brainId, DisplayName = "Alice", Role = MemberRole.Owner };
        await authority.ProvisionOwner(alice);
        await authority.ProvisionOwner(alice);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authority.ProvisionOwner(alice with { PrincipalId = malloryId, DisplayName = "Mallory" }));
        Assert.Null(await authority.Membership(malloryId));
    }

    private sealed class AppCallerAtBirth : ICreationHandler
    {
        public List<CallDecision?> Decisions { get; } = [];
        private AuthorityNeurons? _platform;

        public AppCallerAtBirth Using(AuthorityNeurons platform) { _platform = platform; return this; }

        public async Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
        {
            var scope = kernel.GetPrimaryKeyString();
            var authority = _platform!.BrainAuthority(scope);
            Decisions.Add(await authority.Authorize(Request(created.Owner.AccountId, scope)));
        }

        public static CallRequest Request(string accountId, string brainId) => new()
        {
            Caller = new CallerContext
            {
                PrincipalId = "os",
                AccountId = accountId,
                BrainId = brainId,
                Kind = CallerKind.App,
                StampedBy = TrustedEdge.AppProxy,
                AppId = "settings",
            },
            TargetNeuron = "apps.app2/" + brainId + "/apps/settings",
            Operation = "Pending",
            EnforceTarget = true,
        };
    }
}
