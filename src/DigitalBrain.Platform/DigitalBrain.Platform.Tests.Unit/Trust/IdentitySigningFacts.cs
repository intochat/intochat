using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R63: identity writes cross the trust boundary only signed by a machine bound to the account they act for.
public sealed class IdentitySigningFacts
{
    private static Member Owner(string account, string brain) => new()
    {
        PrincipalId = account, AccountId = account, BrainId = brain, Role = MemberRole.Owner, DisplayName = account,
    };

    [Fact(Timeout = 60_000)]
    public async Task AnUnsignedIdentityWriteIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var authority = trust.Grains.GetGrain<IBrainAuthority>(BrainScope.Create("alice", "home").Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.ProvisionOwner(Owner("alice", "home")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.EnsureMember(Owner("alice", "home") with { PrincipalId = "mallory", Role = MemberRole.Member }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => trust.Grains.GetGrain<IAccount>("alice").EnsureCreated(
            new Account { AccountId = "alice", OwnerPrincipalId = "alice", Name = "alice" }));
        Assert.Null(await authority.Account());
    }

    // R68: spending a Once grant is a write; unsigned, it is refused at the trust boundary.
    [Fact(Timeout = 60_000)]
    public async Task AnUnsignedConsumeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var authority = trust.Grains.GetGrain<IBrainAuthority>(BrainScope.Create("alice", "home").Id);
        var request = new CallRequest
        {
            Caller = new CallerContext { PrincipalId = "app", AccountId = "alice", BrainId = "home", Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy, AppId = "app" },
            TargetNeuron = "alice/home/x", Operation = "read", SemanticTypeIds = ["person.birthDate"],
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authority.Consume(request));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnIdentityWriteFromAMachineNotBoundToTheAccountIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var alice = await brain.PersonAsync("alice", ct);
        using var machine = SequencerFacts.Machine();
        await TrustClientFacts.Register(brain.Services.GetRequiredService<INodeDirectoryClient>(), machine, "mallory", []);
        var mallory = GrainTrustClients.WithScratchCounter(brain.Grains, machine);
        var intruder = Owner(alice.AccountId, alice.BrainId) with { PrincipalId = "mallory", Role = MemberRole.Member };
        var caller = new CallerContext
        {
            PrincipalId = alice.PrincipalId, AccountId = alice.AccountId, BrainId = alice.BrainId, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.EnsureMember(intruder));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.ProvisionOwner(Owner(alice.AccountId, "alice-other")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.GrantAsOwner(caller,
            new DigitalBrain.Platform.Identity.Grant { AppId = "spy", SemanticTypeId = "person.birthDate", Mode = DigitalBrain.Platform.Identity.GrantMode.Always, WorkspaceId = alice.BrainId }));
        Assert.Null(await brain.Services.GetRequiredService<IIdentityClient>().Membership(alice.BrainId, "mallory"));
    }
}
