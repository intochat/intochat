using DigitalBrain.Circles;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Links;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Platform.Tests.Unit.Parts.CircleBrains;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// T26-1..T26-17: a circle is a brain of kind Circle, owned by its creator; its members are accounts recorded on the
// trust side, its machines are contributed, and authorization is the per-brain machinery the circle brain already has.
public sealed class CircleBrainFacts
{
    [Fact(Timeout = 120_000)]
    public async Task CreatingACircleBirthsABrainOwnedByItsCreator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);

        var circle = await Create(brain, alice);

        Assert.Equal(alice.AccountId, (await brain.Get<IBrain>(circle).Read()).Owner?.AccountId);
        var state = await alice.In(circle, () => Circle(brain, circle).State());
        Assert.Equal(("friends", alice.AccountId), (state.Name, state.Creator));
        Assert.Equal([alice.AccountId], state.Members);
        Assert.Equal([brain.Services.GetRequiredService<MachineIdentity>().MachineId], state.Machines);
        Assert.Equal(state.Machines, await brain.Services.GetRequiredService<IIdentityClient>().MachinesOf(circle));
    }

    [Fact(Timeout = 120_000)]
    public async Task ACircleBrainGetsNoOsDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);

        var circle = await Create(brain, alice);

        var (status, apps) = await AppsOf(alice, circle, ct);
        Assert.Equal(200, status);
        Assert.Equal(["circleroom"], apps);
        Assert.Contains(new BrainEntry(circle, BrainKind.Circle), await brain.Services.GetRequiredService<IIdentityClient>().BrainsOf(alice.PrincipalId));
        Assert.Equal(BrainKind.Circle, await brain.Services.GetRequiredService<IIdentityClient>().KindOf(circle));
        await alice.In(circle, () => brain.Get<ICircleChat>(ICircleChat.Key(circle)).Say("hello"));
        Assert.Equal(("hello", alice.AccountId), await alice.In(circle, async () =>
        {
            var line = Assert.Single(await brain.Get<ICircleChat>(ICircleChat.Key(circle)).Read(0));
            return (line.Text, line.Speaker);
        }));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAcceptedInvitationMakesTheAccountAMemberOfTheCircleBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Create(brain, alice);
        var identity = brain.Services.GetRequiredService<IIdentityClient>();

        await alice.In(circle, () => Circle(brain, circle).Invite(bob.AccountId));
        Assert.Null(await identity.Membership(circle, bob.PrincipalId));
        var joined = await bob.In(circle, () => Circle(brain, circle).Accept());

        Assert.Equal([alice.AccountId, bob.AccountId], joined.Members);
        Assert.Empty(joined.Invited);
        var member = await identity.Membership(circle, bob.PrincipalId);
        Assert.Equal((bob.AccountId, circle, MemberRole.Member), (member?.AccountId, member?.BrainId, member?.Role));
        Assert.Contains(new BrainEntry(circle, BrainKind.Circle), await identity.BrainsOf(bob.PrincipalId));
    }

    [Fact(Timeout = 120_000)]
    public async Task AMemberCallsANeuronInTheCircleBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Create(brain, alice);

        await Join(brain, circle, alice, bob);

        var (status, apps) = await AppsOf(bob, circle, ct);
        Assert.Equal(200, status);
        Assert.Equal(["circleroom"], apps);
    }

    [Fact(Timeout = 120_000)]
    public async Task AStrangerIsRefusedByTheCircleBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, carol) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("carol", ct));
        var circle = await Create(brain, alice);

        Assert.Equal(403, (await AppsOf(carol, circle, ct)).Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.In(circle, () => Circle(brain, circle).State()));
    }

    [Fact(Timeout = 120_000)]
    public async Task ARemovedMemberIsRefusedOnItsNextCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        Assert.Equal(200, (await AppsOf(bob, circle, ct)).Status);

        await alice.In(circle, () => Circle(brain, circle).Remove(bob.AccountId));

        Assert.Equal(403, (await AppsOf(bob, circle, ct)).Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(circle, () => Circle(brain, circle).State()));
        Assert.DoesNotContain(circle, (await brain.Services.GetRequiredService<IIdentityClient>().BrainsOf(bob.PrincipalId)).Select(entry => entry.Id));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAcceptWithoutAnInvitationAddsNoMember()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, carol) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("carol", ct));
        var circle = await Create(brain, alice);

        await Assert.ThrowsAsync<InvalidOperationException>(() => carol.In(circle, () => Circle(brain, circle).Accept()));

        Assert.Null(await brain.Services.GetRequiredService<IIdentityClient>().Membership(circle, carol.PrincipalId));
        Assert.Equal([alice.AccountId], (await alice.In(circle, () => Circle(brain, circle).State())).Members);
    }

    [Fact(Timeout = 120_000)]
    public async Task TheCreatorCannotBeRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);

        await Assert.ThrowsAnyAsync<Exception>(() => bob.In(circle, () => Circle(brain, circle).Remove(alice.AccountId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Services.GetRequiredService<IIdentityClient>().RemoveMember(circle, alice.AccountId));

        Assert.Equal([alice.AccountId, bob.AccountId], (await alice.In(circle, () => Circle(brain, circle).State())).Members);
        Assert.NotNull(await brain.Services.GetRequiredService<IIdentityClient>().Membership(circle, alice.PrincipalId));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheCreatorLeavingDeletesTheCircle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        var identity = brain.Services.GetRequiredService<IIdentityClient>();

        await alice.In(circle, () => Circle(brain, circle).Remove(alice.AccountId));

        Assert.Null(await identity.Membership(circle, bob.PrincipalId));
        Assert.Empty(await identity.MachinesOf(circle));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => alice.In(circle, () => Circle(brain, circle).State()));
    }

    // R128: over the trust link, a machine contributed to a circle adds keys to that circle's keyring as an owner
    // machine would, never to its creator's own brain, and not after it is withdrawn.
    [Fact(Timeout = 120_000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task AContributedMachineIsAnOwnerMachineOnlyForItsCircle()
    {
        LinkTestbed.SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var creator = await TrustLinkFacts.RawMachine.Dial(trust, ct);
        await creator.ProvisionOwner("alice", ct);
        await creator.Call<MachineSigned<Account>, bool>(TrustOperations.EnsureAccount,
            creator.Sign(new Account { AccountId = "alice", Name = "alice", OwnerPrincipalId = "alice" }, "alice"), ct);
        var circle = (await creator.Call<MachineSigned<BrainRequest>, BrainProvision>(TrustOperations.CreateBrain,
            creator.Sign(new BrainRequest("alice", "alice", "circle-op", "friends", BrainKind.Circle), "alice"), ct)).Member.BrainId;
        await using var member = await TrustLinkFacts.RawMachine.Dial(trust, ct);
        await member.Register("bob", ct);
        await member.Call<MachineSigned<Member>, Member?>(TrustOperations.EnsureMember,
            member.Sign(new Member { PrincipalId = "bob", AccountId = "bob", BrainId = circle, Role = MemberRole.Member, DisplayName = "bob" }, "bob"), ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AddKey(member, circle, "before", ct));
        await member.Call<MachineSigned<CircleMachine>, bool>(TrustOperations.ContributeMachine, member.Sign(new CircleMachine(circle, member.MachineId), "bob"), ct);
        Assert.True(await AddKey(member, circle, "core", ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AddKey(member, "alice", "own", ct));
        await member.Call<MachineSigned<CircleMachine>, bool>(TrustOperations.WithdrawMachine, member.Sign(new CircleMachine(circle, member.MachineId), "bob"), ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AddKey(member, circle, "after", ct));
    }

    // R133: RemoveMember from a machine bound to the member's account, a contributed machine, or a machine of the
    // circle's owner succeeds; any other machine is refused.
    [Fact(Timeout = 120_000)]
    public async Task OnlyAMemberMachineOrTheOwnerMayRemoveAMember()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var directory = brain.Services.GetRequiredService<INodeDirectoryClient>();
        using var owners = SequencerFacts.Machine();
        using var bobs = SequencerFacts.Machine();
        using var strangers = SequencerFacts.Machine();
        await Trust.TrustClientFacts.Register(directory, owners, "alice", MachineRegistrar.HashOf("alice"));
        await Trust.TrustClientFacts.Register(directory, bobs, "bob", MachineRegistrar.HashOf("bob"));
        await Trust.TrustClientFacts.Register(directory, strangers, "carol", MachineRegistrar.HashOf("carol"));
        await brain.Grains.GetGrain<IAccount>("alice").EnsureCreated(new Account { AccountId = "alice", Name = "alice", OwnerPrincipalId = "alice" });
        var circle = (await brain.Grains.GetGrain<IAccount>("alice").CreateBrain("alice", "circle-op", "friends", BrainKind.Circle)).Member.BrainId;
        var authority = brain.Grains.GetGrain<IBrainAuthority>(circle);
        var writes = brain.Grains.GetGrain<IIdentityWrites>(IIdentityWrites.Key);
        var counter = 0L;
        Task Remove(MachineIdentity machine, string account)
            => writes.RemoveMember(Trust.TrustClientFacts.Sign(machine, new MemberRemoval(circle, account), account, DateTimeOffset.UtcNow, ++counter));
        async Task Member(string account)
            => await authority.EnsureMember(new Member { PrincipalId = account, AccountId = account, BrainId = circle, Role = MemberRole.Member, DisplayName = account });

        await Member("bob");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Remove(strangers, "bob"));
        Assert.NotNull(await authority.Membership("bob"));
        await Remove(bobs, "bob");
        Assert.Null(await authority.Membership("bob"));
        await Member("bob");
        await Remove(owners, "bob");
        Assert.Null(await authority.Membership("bob"));
        await Member("bob");
        await authority.Contribute(strangers.MachineId);
        await Remove(strangers, "bob");
        Assert.Null(await authority.Membership("bob"));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Task<bool> AddKey(TrustLinkFacts.RawMachine machine, string brainId, string partId, CancellationToken ct)
        => machine.Call<(string, string, byte[]), bool>(TrustOperations.KeyAdd, (brainId, partId, [1, 2, 3]), ct);
}
