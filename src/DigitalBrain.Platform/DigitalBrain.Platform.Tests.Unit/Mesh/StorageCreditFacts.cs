using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;
using RawMachine = DigitalBrain.Platform.Tests.Unit.Links.TrustLinkFacts.RawMachine;

namespace DigitalBrain.Platform.Tests.Unit.Mesh;

// T23-11, Q21: a passed audit of a volunteer node is recorded trust-side as one storage credit for the node's owner.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
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
    private static async Task<(RawMachine P, RawMachine X, string Node)> Placed(TrustHost trust, CancellationToken ct)
    {
        var p = await Machine(trust, "alice", ct);
        var x = await Machine(trust, "bob", ct);
        var node = StorageNodes.IdOf(x.MachineId);
        await x.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            x.Sign(new NodeRegistration(node, x.MachineId, "bob", 1 << 30), "bob"), ct);
        Assert.Contains(node, await p.Call<(int, string[], int), string[]>(TrustOperations.NodePlace, (1, Array.Empty<string>(), 1024), ct));
        return (p, x, node);
    }

    private static Task<AuditReport> Audit(RawMachine machine, string node, int round, bool passed, CancellationToken ct)
        => machine.Call<AuditReport, AuditReport>(TrustOperations.NodeAudit, new AuditReport(node, Segment, round, passed, 1024), ct);

    internal static Task<StorageCredit[]> Pending(RawMachine machine, string account, CancellationToken ct)
        => machine.Call<string, StorageCredit[]>(TrustOperations.CreditsPending, account, ct);

    [Fact(Timeout = 120_000)]
    public async Task AnAuditPassRecordsOneCreditForTheNodesOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, 0, true, ct);

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
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, 0, true, ct);
        await Audit(p, node, 0, true, ct);

        Assert.Single(await Pending(x, "bob", ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFailedAuditRecordsNothing()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;

        await Audit(p, node, 0, false, ct);

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
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        var cap = new StorageCreditOptions().MaxPassesPerPutterPerNodePerDay;

        for (var round = 0; round <= cap; round++) { await Audit(p, node, round, true, ct); }
        Assert.Equal(cap, (await Pending(x, "bob", ct)).Length);

        time.Advance(TimeSpan.FromHours(12));
        await Audit(p, node, cap + 1, true, ct);
        Assert.Equal(cap + 1, (await Pending(x, "bob", ct)).Length);
    }

    [Fact(Timeout = 120_000)]
    public async Task PendingCreditsAreReadOnlyByAMachineBoundToTheOwner()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        await Audit(p, node, 0, true, ct);

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
        var (p, x, node) = await Placed(trust, ct);
        await using var _ = p;
        await using var __ = x;
        await Audit(p, node, 0, true, ct);
        var pending = await Pending(x, "bob", ct);

        await x.Call<(string, string[]), bool>(TrustOperations.CreditsAcknowledge, ("bob", pending.Select(credit => credit.EntryId).ToArray()), ct);

        Assert.Empty(await Pending(x, "bob", ct));
        await Audit(p, node, 0, true, ct);
        Assert.Empty(await Pending(x, "bob", ct));
    }
}
