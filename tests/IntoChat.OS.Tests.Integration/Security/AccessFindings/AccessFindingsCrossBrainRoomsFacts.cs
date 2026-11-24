using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.Apps.Social;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// Finding 7 (54-friends-merge-design.md): IRooms.Invited / IRooms.Changed are [Shared] on a [Shareable] contract that
// is NOT [Offered], and RoomsNeuron checks nothing itself. The design note warns that a foreign brain which is not a
// member can therefore write entries into another brain's room index. This fact drives the real silo pipeline with a
// caller stamped as a person of a DIFFERENT brain (the cross-brain tier), through the public IRooms proxy, so the
// AccessGate cross-brain wall is the thing under test -- the call is not made directly in-process.
//
// Named for the property that SHOULD hold: a brain that the victim has not befriended cannot mutate the victim's room
// index. It FAILS today if the write is accepted.
[Trait("Category", "AccessFindings")]
public sealed class AccessFindingsCrossBrainRoomsFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaInvited()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IRooms>(
            host.Services, new NeuronId(victim, "social.rooms", IRooms.DefaultKey));
        var attackerRoom = new NeuronId(attacker, "social.room", "r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            // SHOULD hold: a non-member foreign brain cannot push an entry into the victim's room index.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Invited(attackerRoom, Ct));
        }
    }

    [Fact]
    public async Task AForeignBrain_CannotWriteIntoAnotherBrainsRoomIndex_ViaChanged()
    {
        await using var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct);
        var edge = host.Services.GetRequiredService<EdgeAuthority>();

        var victim = new BrainId(host.Scope);
        var attacker = new BrainId("mallory" + Guid.NewGuid().ToString("N")[..8]);

        var victimRooms = DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IRooms>(
            host.Services, new NeuronId(victim, "social.rooms", IRooms.DefaultKey));
        var attackerRoom = new NeuronId(attacker, "social.room", "r1");

        using (edge.Enter(edge.Person(attacker.Value, attacker.Value, attacker)))
        {
            // SHOULD hold: likewise for the revision-bump path.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => victimRooms.Changed(attackerRoom, 7, Ct));
        }
    }
}
