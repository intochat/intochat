using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Compute;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Trust;
using DigitalBrain.Platform.Tests.Unit.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;
using RawMachine = DigitalBrain.Platform.Tests.Unit.Links.TrustLinkFacts.RawMachine;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// T23-11, Q21: a passed audit of a volunteer node is recorded trust-side as one storage credit for the node's owner.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
[Collection(VolunteerMachines.Name)]
public sealed class StorageCreditFacts
{
    private const string Segment = "sha256:5e";

    private static async Task<RawMachine> Machine(TrustHost trust, string account, CancellationToken ct)
    {
        var machine = await RawMachine.Dial(trust, ct);
        await machine.Register(account, ct);
        return machine;
    }

    // Bob's node X, and alice's machine P placed onto it.
    private static async Task<(RawMachine P, RawMachine X, string Node, string Placement)> Placed(TrustHost trust, CancellationToken ct)
    {
        var p = await Machine(trust, "alice", ct);
        var x = await Machine(trust, "bob", ct);
        var node = StorageNodes.IdOf(x.MachineId);
        await x.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            x.Sign(new NodeRegistration(node, x.MachineId, "bob", 1 << 30), "bob"), ct);
        var placement = await Place(p, ct);
        Assert.Contains(node, placement.Nodes);
        return (p, x, node, placement.Id);
    }

    private static Task<Placement> Place(RawMachine machine, CancellationToken ct)
        => machine.Call<(int, string[], int), Placement>(TrustOperations.NodePlace, (1, Array.Empty<string>(), 1024), ct);

    private static Task<AuditReport> Audit(RawMachine machine, string node, string? placement, int round, bool passed, CancellationToken ct)
        => machine.Call<AuditReport, AuditReport>(TrustOperations.NodeAudit, new AuditReport(node, Segment, round, passed, 1024, placement), ct);

    internal static Task<StorageCredit[]> Pending(RawMachine machine, string account, CancellationToken ct)
        => machine.Call<string, StorageCredit[]>(TrustOperations.CreditsPending, account, ct);

    [Fact(Timeout = 120_000)]
    public async Task AnAuditPassRecordsOneCreditForTheNodesOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, placement, 0, true, ct);

        var credit = Assert.Single(await Pending(x, "bob", ct));
        Assert.Equal($"audit/{node}/{Segment}/0", credit.EntryId);
        Assert.Equal(1L, credit.Micros);
    }

    [Fact(Timeout = 120_000)]
    public async Task AReplayedAuditPassRecordsNothing()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, placement, 0, true, ct);
        await Audit(p, node, placement, 0, true, ct);

        Assert.Single(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFailedAuditRecordsNothing()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, placement, 0, false, ct);

        Assert.Empty(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task PassesBeyondTheDailyCapPerPutterAreNotCredited()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        await using var trust = await TrustHost.StartAsync(ct,
            configure: silo => silo.Services.AddKeyedSingleton<TimeProvider>(MeshOptions.Key, time));
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        var cap = new StorageCreditOptions().MaxPassesPerPutterPerNodePerDay;

        for (var round = 0; round <= cap; round++) { await Audit(p, node, placement, round, true, ct); }
        Assert.Equal(cap, (await Pending(x, "bob", ct)).Length);

        time.Advance(TimeSpan.FromHours(12));
        await Audit(p, node, placement, cap + 1, true, ct);
        Assert.Equal(cap + 1, (await Pending(x, "bob", ct)).Length);
    }

    [Fact(Timeout = 120_000)]
    public async Task PendingCreditsAreReadOnlyByAMachineBoundToTheOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        await Audit(p, node, placement, 0, true, ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Pending(p, "bob", ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            p.Call<(string, string[]), bool>(TrustOperations.CreditsAcknowledge, ("bob", [$"audit/{node}/{Segment}/0"]), ct));
        Assert.Single(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AcknowledgedCreditsAreNotPendingAgain()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        await Audit(p, node, placement, 0, true, ct);
        var pending = await Pending(x, "bob", ct);

        await x.Call<(string, string[]), bool>(TrustOperations.CreditsAcknowledge, ("bob", pending.Select(credit => credit.EntryId).ToArray()), ct);

        Assert.Empty(await Pending(x, "bob", ct));
        await Audit(p, node, placement, 0, true, ct);
        Assert.Empty(await Pending(x, "bob", ct));
    }

    // R142: a pass earns credit only under a placement the directory issued to the reporting account, holding the node.
    [Fact(Timeout = 120_000)]
    public async Task AReportNamingAPlacementTheDirectoryNeverIssuedEarnsNoCredit()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, _) = await Placed(trust, ct);
        await using var __ = p;
        await using var ___ = x;

        await Audit(p, node, Guid.NewGuid().ToString("N"), 0, true, ct);
        await Audit(p, node, null, 1, true, ct);

        Assert.Empty(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AReportNamingAnotherAccountsPlacementEarnsNoCredit()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, _) = await Placed(trust, ct);
        await using var __ = p;
        await using var ___ = x;
        await using var carol = await Machine(trust, "carol", ct);
        var carols = await Place(carol, ct);
        Assert.Contains(node, carols.Nodes);

        await Audit(p, node, carols.Id, 0, true, ct);

        Assert.Empty(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AReportForANodeOutsideThePlacementEarnsNoCredit()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, _) = await Placed(trust, ct);
        await using var __ = p;
        await using var ___ = x;
        // A placement of alice's that does not hold bob's node (only seeds are left once it is excluded).
        var elsewhere = await p.Call<(int, string[], int), Placement>(TrustOperations.NodePlace, (1, [node], 1024), ct);
        Assert.DoesNotContain(node, elsewhere.Nodes);

        await Audit(p, node, elsewhere.Id, 0, true, ct);

        Assert.Empty(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheSameRoundIsCreditedOnce()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node, placement) = await Placed(trust, ct);
        await using var __ = p;
        await using var ___ = x;
        var again = await Place(p, ct);
        Assert.Contains(node, again.Nodes);

        await Audit(p, node, placement, 0, true, ct);
        await Audit(p, node, again.Id, 0, true, ct);

        Assert.Equal($"audit/{node}/{Segment}/0", Assert.Single(await Pending(x, "bob", ct)).EntryId);
    }

    // The pull (T23-11, R76, R113): the owner account's core holder moves its pending credits into its ledger once.
    private static readonly string Owner = AccountSession.DefaultLogin;

    // Owner's core holder H, owner's node X, and alice's machine P placed onto X.
    private static async Task<(ITestBrain H, RawMachine P, RawMachine X, string Node, string Placement)> OwnerPlaced(TrustHost trust, CancellationToken ct)
    {
        var h = await TestBrain.Create().WithTrust(trust).AsMachine().WithConfiguration(SecondMachineFacts.CoreHolder, "true").StartAsync(ct);
        await h.Services.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var p = await Machine(trust, "alice", ct);
        var x = await Machine(trust, Owner, ct);
        var node = StorageNodes.IdOf(x.MachineId);
        await x.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            x.Sign(new NodeRegistration(node, x.MachineId, Owner, 1 << 30), Owner), ct);
        var placement = await Place(p, ct);
        Assert.Contains(node, placement.Nodes);
        return (h, p, x, node, placement.Id);
    }

    private static ILedgerGrain Ledger(ITestBrain machine) => machine.Grains.GetGrain<ILedgerGrain>(Owner);

    private static Task Pull(ITestBrain machine, CancellationToken ct) => machine.Services.GetRequiredService<StorageCreditPuller>().Pass(ct);

    [Fact(Timeout = 180_000)]
    public async Task AnAuditPassCreditsOnce()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (h, p, x, node, placement) = await OwnerPlaced(trust, ct);
        await using var _ = h;
        await using var __ = p;
        await using var ___ = x;
        await Audit(p, node, placement, 0, true, ct);
        await Audit(p, node, placement, 0, true, ct);
        var puller = h.Services.GetRequiredService<StorageCreditPuller>();

        puller.BeforeAcknowledge = () => throw new TimeoutException("the acknowledge was lost");
        await Pull(h, ct);
        puller.BeforeAcknowledge = null;
        await Pull(h, ct);

        var entry = Assert.Single(await Ledger(h).Entries(10));
        Assert.Equal(($"audit/{node}/{Segment}/0", 1L), (entry.EntryId, entry.Micros));
        Assert.Equal(1L, await Ledger(h).Balance());
        Assert.True(await Ledger(h).Verify());
        Assert.Empty(await Pending(x, Owner, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task AFailedAuditCreditsNothing()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (h, p, x, node, placement) = await OwnerPlaced(trust, ct);
        await using var _ = h;
        await using var __ = p;
        await using var ___ = x;
        await Audit(p, node, placement, 0, false, ct);

        await Pull(h, ct);

        Assert.Empty(await Ledger(h).Entries(10));
        Assert.True(await Ledger(h).Verify());
    }

    [Fact(Timeout = 180_000)]
    public async Task OnlyACoreHolderOfTheOwnersAccountPullsCredits()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (h, p, x, node, placement) = await OwnerPlaced(trust, ct);
        await using var _ = h;
        await using var __ = p;
        await using var ___ = x;
        await using var b = await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct);
        await b.Services.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        Assert.DoesNotContain(b.Services.GetRequiredService<PartLogs>().Opened, part => part.IsCore);
        await Audit(p, node, placement, 0, true, ct);

        await Pull(b, ct);

        Assert.Empty(await Ledger(b).Entries(10));
        Assert.Single(await Pending(x, Owner, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task TheCoreHolderOfAnyOfTheAccountsBrainsPulls()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (h, p, x, node, placement) = await OwnerPlaced(trust, ct);
        await using var _ = h;
        await using var __ = p;
        await using var ___ = x;
        await using var b = await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct);
        await b.Services.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var second = BrainScope.Create(Owner, "second").Id;
        await b.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().ProvisionOwner(
            new DigitalBrain.Framework.Platform.Identity.Member { PrincipalId = Owner, AccountId = Owner, BrainId = "second", Role = DigitalBrain.Framework.Platform.Identity.MemberRole.Owner, DisplayName = Owner });
        Assert.Equal(Owner, await b.Services.GetRequiredService<IKeyringClient>().OwnerOf(second));
        Assert.False((await b.Services.GetRequiredService<PartLogs>().For(PartId.Core(second))).Closed);
        await Audit(p, node, placement, 0, true, ct);

        await Pull(b, ct);
        await Pull(h, ct);

        Assert.Equal($"audit/{node}/{Segment}/0", Assert.Single(await Ledger(b).Entries(10)).EntryId);
        Assert.Empty(await Ledger(h).Entries(10));
        await Audit(p, node, placement, 1, true, ct);
        await Pull(h, ct);
        await Pull(b, ct);
        Assert.Equal($"audit/{node}/{Segment}/1", Assert.Single(await Ledger(h).Entries(10)).EntryId);
        Assert.Single(await Ledger(b).Entries(10));
    }
}
