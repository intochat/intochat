using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// Task 21 Step 8, loopback: two own machines and a trust host in one process hand a part back and forth, and
// every head they commit is a CompareAndSet served by the trust host's link endpoint for that machine.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class LinkLoopbackFacts
{
    [Fact(Timeout = 180_000)]
    public async Task TwoMachinesCommitThroughTheTrustLinkEndpoint()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var served = new ConcurrentBag<(string Operation, string? Machine, ActivityTraceId Trace)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TrustLinkEndpoint.SourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => served.Add((activity.DisplayName, activity.GetTagItem("digitalbrain.machine") as string, activity.TraceId)),
        };
        ActivitySource.AddActivityListener(listener);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        var (idA, idB) = (a.SiloServices.GetRequiredService<IMachineTransport>().LocalMachineId, b.SiloServices.GetRequiredService<IMachineTransport>().LocalMachineId);
        var part = PartId.ForApp(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id, "counter");
        var grain = $"counter/{part.Value}/c1";

        var logsA = a.SiloServices.GetRequiredService<PartLogs>();
        await (await logsA.For(part)).Append(grain, "counter", RecordChange.Write, [3], []);
        await logsA.Release(part);
        var logsB = b.SiloServices.GetRequiredService<PartLogs>();
        Assert.Equal([3], (await logsB.For(part)).Read(grain, "counter")!.Record);
        await (await logsB.For(part)).Append(grain, "counter", RecordChange.Write, [4], []);
        await logsB.Release(part);
        Assert.Equal([4], (await logsA.For(part)).Read(grain, "counter")!.Record);

        var compareAndSets = served.Where(span => span.Operation == $"{nameof(TrustLinkEndpoint)} {TrustOperations.CompareAndSet}").ToArray();
        Assert.Contains(compareAndSets, span => span.Machine == idA);
        Assert.Contains(compareAndSets, span => span.Machine == idB);
    }
}
