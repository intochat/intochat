using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Modules.Time;
using Microsoft.Extensions.DependencyInjection;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;

namespace IntoChat.OS.Kernel.Tests.Integration.Hosting;

// 06-09, 06 D-15 as amended by R4-11 (owner 2026-10-25): a plain os neuron call through Get<T>(NeuronId) costs at most 5x the p50
// of a typed kernel grain call and at most 4 KiB of extra allocation per call for payloads up to 1 KiB. Same method as
// 04-HOST-PROOF: one in-process silo (SiloHost.Local), warmup, then interleaved rounds that alternate which path goes first,
// p50 from per-call Stopwatch ticks and bytes from GC.GetTotalAllocatedBytes(precise) per block. The argument-free pair here
// (ITimer.Stop against IBrainEntries.Version) is the cheapest call on each path, so it isolates the fixed dispatch cost; the
// payload rows are measured by the framework's HostCostFacts. Explicit: it is a measurement, not a PR gate. The live-host
// re-measure is 06-10.
public sealed class HostDispatchCostFacts
{
    private const int Warmup = 10_000;
    private const int Measured = 100_000;
    private const int Round = 1_000;
    private const double MaxRatio = 5.0;
    private const double MaxExtraBytes = 4096;

    [Fact(Explicit = true)]
    public async Task HostDispatchCost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>().StartAsync(ct);
        var typed = brain.Services.GetRequiredService<IntoChat.OS.Runtime.IStillGrains>().Get<IBrainEntries>("cost");
        var host = brain.Get<ITimer>(brain.Owner.Key("timer/cost"));

        for (var i = 0; i < Warmup; i++)
        {
            await typed.Version();
            await host.Stop();
        }

        var ticks = new[] { new long[Measured], new long[Measured] };
        var counts = new int[2];
        var bytes = new long[2];
        for (var round = 0; round < Measured / Round; round++)
        {
            ct.ThrowIfCancellationRequested();
            for (var turn = 0; turn < 2; turn++)
            {
                var which = (round + turn) % 2;
                var before = GC.GetTotalAllocatedBytes(precise: true);
                for (var i = 0; i < Round; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    if (which == 0) { await typed.Version(); } else { await host.Stop(); }
                    ticks[which][counts[which]++] = Stopwatch.GetTimestamp() - start;
                }
                bytes[which] += GC.GetTotalAllocatedBytes(precise: true) - before;
            }
        }

        var (t50, t99, tb) = Stats(ticks[0], bytes[0]);
        var (h50, h99, hb) = Stats(ticks[1], bytes[1]);
        var ratio = h50 / t50;
        var extra = hb - tb;
        var report = string.Join(Environment.NewLine,
            FormattableString.Invariant($"machine: {Environment.MachineName}, {Environment.ProcessorCount} logical cores, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}"),
            FormattableString.Invariant($"warmup {Warmup}, measured {Measured} per path, interleaved rounds of {Round}"),
            "| call | path | p50 us | p99 us | B/call | p50 ratio | extra B/call |",
            "|---|---|---|---|---|---|---|",
            FormattableString.Invariant($"| no payload | typed kernel grain (IBrainEntries.Version) | {t50:F2} | {t99:F2} | {tb:F0} | 1.00 | 0 |"),
            FormattableString.Invariant($"| no payload | plain neuron (ITimer.Stop) | {h50:F2} | {h99:F2} | {hb:F0} | {ratio:F2} | {extra:F0} |"));
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "intochat-host-dispatch-cost.md"), report, ct);

        var ratioMessage = string.Create(CultureInfo.InvariantCulture, $"plain-neuron p50 is {ratio:F2}x the typed call; budget {MaxRatio}x (R4-11).");
        var extraMessage = string.Create(CultureInfo.InvariantCulture, $"plain-neuron call allocates {extra:F0} B more than the typed call; budget {MaxExtraBytes} B (R4-11).");
        Assert.True(ratio <= MaxRatio, ratioMessage);
        Assert.True(extra <= MaxExtraBytes, extraMessage);
    }

    private static (double P50, double P99, double Bytes) Stats(long[] ticks, long bytes)
    {
        Array.Sort(ticks);
        static double Us(long value) => value * 1_000_000.0 / Stopwatch.Frequency;
        return (Us(ticks[Measured / 2]), Us(ticks[(int)(Measured * 0.99)]), (double)bytes / Measured);
    }
}
