using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Time;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace DigitalBrain.Modules.Time.Tests;

public sealed class InventoryFacts
{
    [Fact]
    public async Task IndexWriteFailuresDoNotStrandSchedulingOrCancellationAndRestoreTheReadModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var faults = new InventoryWrites();
        var control = new ControlledTimers();
        await using var host = await ModuleTest.Create().WithModule<TimeModule>().WithReminders().ConfigureSilo(silo =>
        {
            silo.UseControlledTimers(control);
            var original = silo.Services.Last(service => service.ServiceType == typeof(IGrainStorage) && Equals(service.ServiceKey, "Default"));
            silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, key) =>
                new FailingInventoryStorage((IGrainStorage)original.KeyedImplementationFactory!(services, key), faults));
        }).StartAsync(ct);
        Stamp("one");
        await host.AuthorizeCallerAsync();
        var timer = host.Get<ITimer>(BrainScope.CurrentId() + "/failure");
        var provider = host.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "time");
        faults.Fail = true;
        await timer.Start(TimeSpan.FromHours(1));
        Assert.Empty(await provider.Persisted(null, ct));
        Assert.False(control.For(timer.GetGrainId()).IsDisposed);
        faults.Fail = false;
        await timer.Start(TimeSpan.FromHours(2));
        var before = Assert.Single(await provider.Persisted(null, ct));
        faults.Fail = true;
        await timer.Start(TimeSpan.FromHours(3));
        Assert.Equal(before, Assert.Single(await provider.Persisted(null, ct)));
        Assert.Equal(TimeSpan.FromHours(3), control.For(timer.GetGrainId()).Options.DueTime);
        await timer.Stop();
        Assert.True(control.For(timer.GetGrainId()).IsDisposed);
        Assert.Equal(before, Assert.Single(await provider.Persisted(null, ct)));
        faults.Fail = false;
        await timer.Stop();
        Assert.Empty(await provider.Persisted(null, ct));
    }

    private sealed class InventoryWrites { public bool Fail { get; set; } }
    private sealed class FailingInventoryStorage(IGrainStorage inner, InventoryWrites faults) : IGrainStorage
    {
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ReadStateAsync(name, id, state);
        public Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state) => name == "schedules" && faults.Fail
            ? throw new IOException("Index write refused") : inner.WriteStateAsync(name, id, state);
        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ClearStateAsync(name, id, state);
    }

    [Fact]
    public async Task InventoryKeepsDurableReminderIntentScopedAndCancellationRemovesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().WithModule<TimeModule>().WithReminders().StartAsync(ct);
        foreach (var name in new[] { "one", "two" })
        {
            Stamp(name);
            await host.AuthorizeCallerAsync();
            var scope = BrainScope.CurrentId();
            await host.Get<IReminder>(scope + "/standup").Start(TimeSpan.FromHours(1), TimeSpan.FromDays(1));
            if (name == "one") { await host.Get<IReminder>(scope + "/review").Start(TimeSpan.FromHours(2), TimeSpan.FromDays(1)); }
        }
        var provider = host.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "time");
        foreach (var name in new[] { "one", "two" })
        {
            Stamp(name);
            var scope = BrainScope.CurrentId();
            var records = await provider.Persisted("reminder", ct);
            Assert.Equal(name == "one" ? 2 : 1, records.Length);
            Assert.All(records, record => { Assert.StartsWith(scope + "/", record.Key, StringComparison.Ordinal); Assert.Contains("fires", record.Display, StringComparison.Ordinal); });
            Assert.Equal(records.Length, (await provider.Active("reminder", ct)).Length);
            var reminder = host.Get<IReminder>(scope + "/standup");
            await host.DeactivateAsync(reminder, ct);
            await host.DeactivateAsync(host.Get<ITimeInventory>(scope), ct);
            Assert.DoesNotContain(await provider.Active("reminder", ct), record => record.Key == scope + "/standup");
            Assert.Contains(await provider.Persisted("reminder", ct), record => record.Key == scope + "/standup");
            await reminder.Stop();
            Assert.DoesNotContain(await provider.Persisted(null, ct), record => record.Key == scope + "/standup");
            Assert.Empty(await provider.Persisted("other", ct));
        }
    }

    [Fact]
    public async Task TimerFinalFireRemovesOnlyItsOwnIndexedSchedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var control = new ControlledTimers();
        await using var host = await ModuleTest.Create().WithModule<TimeModule>().WithReminders()
            .ConfigureSilo(silo => silo.UseControlledTimers(control)).StartAsync(ct);
        Stamp("one");
        await host.AuthorizeCallerAsync();
        var timer = host.Get<ITimer>(BrainScope.CurrentId() + "/once");
        await timer.Start(TimeSpan.FromHours(1));
        var provider = host.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "time");
        Assert.Single(await provider.Persisted("timer", ct));
        await control.For(timer.GetGrainId()).FireAsync(ct);
        Assert.Empty(await provider.Persisted("timer", ct));
    }

    private static void Stamp(string brain) => CallerContextStamper.Stamp(new()
    { PrincipalId = "owner", AccountId = "owner", BrainId = brain, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
}
