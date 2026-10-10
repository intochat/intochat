using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R74: every machine of the owner opens a part under the one key the keyring holds for it.
public sealed class SecondMachineFacts
{
    [Fact(Timeout = 180_000)]
    public async Task TwoMachinesRacingForAPartKeyEndWithOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        var aKeys = a.SiloServices.GetRequiredService<IPartKeys>();
        var bKeys = b.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "race");

        var keys = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? aKeys : bKeys).Key(part, ModuleTest.DefaultAccount, ct)));

        Assert.Single(keys.Select(Convert.ToBase64String).Distinct());
    }

    [Fact(Timeout = 180_000)]
    public async Task AMachineWhoseCustodyCannotUnwrapAPartKeyIsRefusedClearly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
            .StartAsync(ct);
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "custody");
        var aLogs = a.SiloServices.GetRequiredService<PartLogs>();
        await (await aLogs.For(part)).Append($"counter/{part.Value}/c1", "counter", RecordChange.Write, [1], []);
        await aLogs.Release(part);

        var refused = await Assert.ThrowsAsync<ServiceUnavailableException>(() => b.SiloServices.GetRequiredService<PartLogs>().For(part));
        Assert.Contains("not unwrappable on this machine", refused.Message, StringComparison.Ordinal);
    }
}
