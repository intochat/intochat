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
        await using var machine = await TestBrain.Create().WithTrust(trust).WithPartLog().WithConfiguration(SecondMachineFacts.CoreHolder, "true")
            .ConfigureServices(services => services.AddSingleton<ICreationHandler>(handler))
            .StartAsync(ct);

        await machine.SiloServices.GetRequiredService<DigitalBrain.Platform.Trust.OwnerBirth>().Born.WaitAsync(ct);
        var created = Assert.Single(handler.Brains);
        Assert.Equal(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id, created);
    }

    // Round 3 (f): the owner brain's creation handlers run after the silo is up, so a slow one (the OS defaults
    // waiting on the mesh or an integration) never holds the silo's start or its seeders.
    [Fact(Timeout = 120_000)]
    public async Task ASlowCreationHandlerDoesNotHoldTheSiloStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new Gate();
        await using var machine = await TestBrain.Create()
            .ConfigureServices(services => services.AddSingleton<ICreationHandler>(gate))
            .StartAsync(ct);

        await gate.Entered.Task.WaitAsync(ct);
        Assert.False(machine.SiloServices.GetRequiredService<DigitalBrain.Platform.Trust.OwnerBirth>().Born.IsCompleted);
        gate.Release.SetResult();
        await machine.SiloServices.GetRequiredService<DigitalBrain.Platform.Trust.OwnerBirth>().Born.WaitAsync(ct);
    }

    // Round 4: the boot-time birth stamps no caller of its own, so the creation handlers (the OS defaults' installs,
    // which read the caller's brain) would run callerless; it is born as its owner.
    [Fact(Timeout = 120_000)]
    public async Task TheOwnerBrainIsBornAsItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new Recording();
        await using var machine = await TestBrain.Create()
            .ConfigureServices(services => services.AddSingleton<ICreationHandler>(handler))
            .StartAsync(ct);

        await machine.SiloServices.GetRequiredService<DigitalBrain.Platform.Trust.OwnerBirth>().Born.WaitAsync(ct);
        Assert.Equal([AccountSession.DefaultLogin], handler.Callers);
    }

    private sealed class Gate : ICreationHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class Recording : ICreationHandler
    {
        public List<string> Brains { get; } = [];

        public List<string?> Callers { get; } = [];

        public Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
        {
            lock (Brains)
            {
                Brains.Add(kernel.GetPrimaryKeyString());
                Callers.Add(CallerContextStamper.TryGet(out var caller) ? caller.AccountId : null);
            }
            return Task.CompletedTask;
        }
    }
}
