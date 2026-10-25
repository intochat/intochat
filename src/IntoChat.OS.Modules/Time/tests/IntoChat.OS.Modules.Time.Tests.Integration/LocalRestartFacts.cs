using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Modules.Time.Reminders.Signals;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace IntoChat.OS.Modules.Time.Tests.Integration;

// D-25 / TOPO-02, in-process tier: Topology=local (the digitalbrain LocalProfile: file grain stores and the file
// reminder table under the TestHome) survives a silo restart over the same Home. Reminders, never timers: a timer
// dies with its activation (research F7). Missed ticks are not replayed; the next scheduled tick is.
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
        Assert.IsType<global::DigitalBrain.Runtime.Orleans.LocalProfile>(brain.Services.GetRequiredService<global::DigitalBrain.Runtime.Orleans.ITopologyProfile>());
        using var person = await brain.PersonAsync("restart-owner", ct);
        var created = (await brain.Services.GetRequiredService<IIdentityClient>()
            .CreateBrain(person.AccountId, person.PrincipalId, "restart", "Restart", BrainKind.Personal)).Member.BrainId;
        using (person.AsCallerIn(created)) { await brain.Get<IBrain>(created).Create(new Owner(person.AccountId)); }

        int before;
        await using (var ticks = await brain.Observe<ReminderTick>(brain.Get<IReminder>("restart"), ct))
        {
            await brain.Get<IReminder>("restart").Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            await ticks.NextAsync(ct: ct).WaitAsync(Bound, ct);
            before = ticks.Snapshot.Count;
        }
        Assert.True(before >= 1);

        await brain.RestartAsync(ct);

        var listed = await brain.Services.GetRequiredService<IIdentityClient>().BrainsOf(person.PrincipalId);
        Assert.Contains(listed, entry => entry.Id == created);
        await using var after = await brain.Observe<ReminderTick>(brain.Get<IReminder>("restart"), ct);
        Assert.Equal("restart", (await after.NextAsync(ct: ct).WaitAsync(Bound, ct)).ReminderId);
        Assert.True(before + after.Snapshot.Count > before);
        await brain.Get<IReminder>("restart").Stop();
    }
}
