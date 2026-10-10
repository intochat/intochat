using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// R32/H3: two machines of one trust cluster hand an app part back and forth at the part-log level. What one wrote
// the other reads from the mesh with the part key from the keyring; neither ever holds the other's core.
public sealed class MachineHandoverFacts
{
    [Fact(Timeout = 180_000)]
    public async Task AMachineRunsAPartFromAnotherMachinesLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        var scope = BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;
        await a.Grains.GetGrain<IBrain>(scope).Create(new Owner(ModuleTest.DefaultAccount));

        var key = $"{scope}/apps/counter/c1";
        var part = PartId.Of(key);
        var grain = $"counter/{key}";
        var aLogs = a.SiloServices.GetRequiredService<PartLogs>();
        var bLogs = b.SiloServices.GetRequiredService<PartLogs>();

        await (await aLogs.For(part)).Append(grain, "counter", RecordChange.Write, [3], []);
        await aLogs.Release(part);
        Assert.DoesNotContain(part, aLogs.Opened);

        var onB = await bLogs.For(part);
        Assert.Equal([3], onB.Read(grain, "counter")!.Record);
        await onB.Append(grain, "counter", RecordChange.Write, [4], []);
        await bLogs.Release(part);

        Assert.Equal([4], (await aLogs.For(part)).Read(grain, "counter")!.Record);
        Assert.DoesNotContain(PartId.Core(scope), bLogs.Opened);
        Assert.Contains(PartId.Core(scope), aLogs.Opened);
    }
}
