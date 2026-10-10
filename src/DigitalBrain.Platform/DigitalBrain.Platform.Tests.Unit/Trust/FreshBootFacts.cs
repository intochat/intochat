using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// A fresh machine's owner brain is born at boot (R59), so the creation handlers that install the OS defaults and
// boot the features run without waiting for a first call.
public sealed class FreshBootFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AFreshMachineBootsItsFeatures()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new Recording();
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await ModuleTest.Create().WithTrust(trust).WithPartLog()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ICreationHandler>(handler))
            .StartAsync(ct);

        var created = Assert.Single(handler.Brains);
        Assert.Equal(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, created);
    }

    private sealed class Recording : ICreationHandler
    {
        public List<string> Brains { get; } = [];

        public Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
        {
            lock (Brains) { Brains.Add(kernel.GetPrimaryKeyString()); }
            return Task.CompletedTask;
        }
    }
}
