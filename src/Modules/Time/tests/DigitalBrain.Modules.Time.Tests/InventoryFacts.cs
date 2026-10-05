using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Time;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace DigitalBrain.Modules.Time.Tests;

public sealed class InventoryFacts
{
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
