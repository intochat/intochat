using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Modules.Time.Reminders.Signals;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Xunit;

namespace IntoChat.OS.Modules.Time.Tests.Integration;

// D-25 / TOPO-02, in-process tier: Topology=local (the digitalbrain LocalProfile: file grain stores and the file
// reminder table under the TestHome) survives a silo restart over the same Home. Reminders, never timers: a timer
// dies with its activation (research F7). Missed ticks are not replayed; the next scheduled tick is.
// Since 06-03 the reminder is the plain ReminderNeuron on the runtime's host grain, reached through the os host's
// IPlainNeurons, and its ticks are read from the brain's BrainLog.
[Trait("host", "own")]
public sealed class LocalRestartFacts
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ABrainAndItsReminderSurviveARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>()
            // Test only: the production minimum stays the Orleans default.
            .ConfigureServices(services => services.Configure<ReminderOptions>(options => options.MinimumReminderPeriod = TimeSpan.FromSeconds(1)))
            .StartAsync(ct);
        // The test host leaves IntoChatOS:Topology unset, which is local.
        Assert.IsType<LocalProfile>(brain.Services.GetRequiredService<ITopologyProfile>());
        using var person = await brain.PersonAsync("restart-owner", ct);
        var created = (await brain.Services.GetRequiredService<IIdentityClient>()
            .CreateBrain(person.AccountId, person.PrincipalId, "restart", "Restart", BrainKind.Personal)).Member.BrainId;
        using (person.AsCallerIn(created)) { await brain.Get<IBrain>(IBrain.Key(created)).Create(new Owner(person.AccountId)); }
        var key = created + "/restart";

        await Reminder(brain, key).Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var before = await Ticks(brain, key, 1, ct);
        Assert.True(before >= 1);

        await brain.RestartAsync(ct);

        var listed = await brain.Services.GetRequiredService<IIdentityClient>().BrainsOf(person.PrincipalId);
        Assert.Contains(listed, entry => entry.Id == created);
        Assert.True(await Ticks(brain, key, before + 1, ct) > before);
        await Reminder(brain, key).Stop();
    }

    internal static IReminder Reminder(ITestBrain brain, string key)
        => (IReminder)brain.Services.GetRequiredService<IPlainNeurons>().Get(typeof(IReminder), key);

    // Waits until the reminder at the os key has journaled at least count ticks; returns how many it has.
    internal static async Task<int> Ticks(ITestBrain brain, string key, int count, CancellationToken ct)
    {
        var id = brain.Services.GetRequiredService<IPlainNeurons>().IdOf(typeof(IReminder), key);
        var journal = brain.Services.GetRequiredService<IBrainJournalWriter>();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(Bound);
        while (true)
        {
            await journal.Claim(id.Brain.Value, bound.Token);
            var page = await journal.ReadSince(id.Brain.Value, id.ToString(), 0, 1_000, bound.Token);
            var ticks = page.Signals.Where(entry => entry.Type == "time.reminder-tick")
                .Select(entry => JsonSerializer.Deserialize<ReminderTick>(entry.Json.Span, RuntimeProtocol.Json)!).ToArray();
            Assert.All(ticks, tick => Assert.Equal($"{id.Brain.Value}/{id.Key}", tick.ReminderId));
            if (ticks.Length >= count) { return ticks.Length; }
            await Task.Delay(100, bound.Token);
        }
    }
}
