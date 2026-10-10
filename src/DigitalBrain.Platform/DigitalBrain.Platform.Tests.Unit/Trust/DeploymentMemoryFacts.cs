using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R75: a machine remembers each trust deployment apart. A fresh deployment meets a clean memory under a kept
// machine directory; the same deployment serving an old head still trips the rollback check (Q10).
public sealed class DeploymentMemoryFacts
{
    private static readonly string Directory = $"{PartsOptions.Key}:{nameof(PartsOptions.MachineDirectory)}";

    [Fact(Timeout = 180_000)]
    public async Task AFreshTrustDeploymentDoesNotTripAnOldMachinesRollbackMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineDirectory = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}");
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "counter");
        var grain = $"counter/{part.Value}/c1";
        await using (var first = await TrustHost.StartAsync(ct))
        await using (var machine = await ModuleTest.Create().WithTrust(first).WithPartLog().WithConfiguration(Directory, machineDirectory).StartAsync(ct))
        {
            var logs = machine.SiloServices.GetRequiredService<PartLogs>();
            for (var round = 0; round < 3; round++)
            {
                await (await logs.For(part)).Append(grain, "counter", RecordChange.Write, [(byte)round], []);
                await logs.Release(part);
            }
        }

        await using var second = await TrustHost.StartAsync(ct);
        await using var again = await ModuleTest.Create().WithTrust(second).WithPartLog().WithConfiguration(Directory, machineDirectory).StartAsync(ct);
        var fresh = again.SiloServices.GetRequiredService<PartLogs>();
        await (await fresh.For(part)).Append(grain, "counter", RecordChange.Write, [9], []);
        await fresh.Release(part);

        Assert.Equal([9], (await fresh.For(part)).Read(grain, "counter")!.Record);
    }

    [Fact(Timeout = 180_000)]
    public async Task TheSameDeploymentStillDetectsARollback()
    {
        var ct = TestContext.Current.CancellationToken;
        var machineDirectory = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}");
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "counter");
        await using var trust = await TrustHost.StartAsync(ct);
        string deployment;
        await using (var machine = await ModuleTest.Create().WithTrust(trust).WithPartLog().WithConfiguration(Directory, machineDirectory).StartAsync(ct))
        {
            deployment = await machine.SiloServices.GetRequiredService<INodeDirectoryClient>().DeploymentId();
            var logs = machine.SiloServices.GetRequiredService<PartLogs>();
            await (await logs.For(part)).Append($"counter/{part.Value}/c1", "counter", RecordChange.Write, [1], []);
            await logs.Release(part);
        }
        // This machine once saw a later head of the part than the deployment now serves.
        var seen = Path.Combine(machineDirectory, deployment, "seen.json");
        var versions = JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(seen, ct))!;
        versions[HeadRecord.HashOf(part.Value)] = 1_000;
        await File.WriteAllTextAsync(seen, JsonSerializer.Serialize(versions), ct);

        await using var again = await ModuleTest.Create().WithTrust(trust).WithPartLog().WithConfiguration(Directory, machineDirectory).StartAsync(ct);

        await Assert.ThrowsAsync<RollbackDetectedException>(() => again.SiloServices.GetRequiredService<PartLogs>().For(part));
    }
}
