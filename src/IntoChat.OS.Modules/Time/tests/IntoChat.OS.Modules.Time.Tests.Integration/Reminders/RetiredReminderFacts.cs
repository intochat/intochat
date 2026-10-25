using IntoChat.OS.Modules.Time.Reminders;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Timers;

namespace IntoChat.OS.Modules.Time.Tests.Integration;

// SAFE-01 for the reminder table (06-03): a row the Framework ReminderNeuron registered before the migration (grain id
// reminder/{brain}/{key}, reminder "tick") still fires after it. The kernel composition's retired-reminder grain moves the
// row onto the plain neuron's host grain (neuron.host/{brain}/reminder/{key}) and the plain neuron publishes the tick. Only the
// reminder-table key and grain ids are asserted; grain state is a clean break and is not carried.
[Trait("host", "own")]
public sealed class RetiredReminderFacts
{
    [Fact]
    public async Task AReminderRegisteredByTheFrameworkNeuronFiresOnThePlainNeuronAfterTheMigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>()
            .ConfigureServices(services => services.Configure<ReminderOptions>(options => options.MinimumReminderPeriod = TimeSpan.FromSeconds(1)))
            .StartAsync(ct);
        var key = "retired-brain/kitchen";
        var legacy = GrainId.Create("reminder", key);
        var host = GrainId.Create("neuron.host", "retired-brain/reminder/kitchen");
        // The row exactly as the Framework neuron wrote it: its own grain id, the reminder "tick". It is written into the
        // reminder table and the silo restarts over it, as a host upgraded past the migration would start.
        await brain.Services.GetRequiredService<IReminderTable>().UpsertRow(new ReminderEntry
        {
            GrainId = legacy,
            ReminderName = "tick",
            StartAt = DateTime.UtcNow.AddSeconds(1),
            Period = TimeSpan.FromSeconds(2),
        }, ct);
        await brain.RestartAsync(ct);
        var table = brain.Services.GetRequiredService<IReminderTable>();

        Assert.True(await LocalRestartFacts.Ticks(brain, key, 1, ct) >= 1);

        Assert.Empty((await table.ReadRows(legacy, ct)).Reminders);
        var moved = Assert.Single((await table.ReadRows(host, ct)).Reminders);
        Assert.Equal(("tick", TimeSpan.FromSeconds(2)), (moved.ReminderName, moved.Period));
        Assert.True(await LocalRestartFacts.Ticks(brain, key, 2, ct) >= 2);
        await LocalRestartFacts.Reminder(brain, key).Stop();
        Assert.Empty((await table.ReadRows(host, ct)).Reminders);
    }
}
