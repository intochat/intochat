using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Testing.Tests.Unit.Confirmation;
using OS = DigitalBrain.Testing.TestOS;

namespace DigitalBrain.Testing.Tests.Unit.Hosting;

[NeuronAlias("test.tick-witness")]
public interface ITickWitness
{
    Task Arm();
}

// Records the caller its reminder tick runs as.
[Neuron("test.tick-witness")]
public sealed class TickWitnessNeuron : Neuron, ITickWitness
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Caller?> Ticks = new();

    public Task Arm() => Context.Reminders.Start("witness", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    protected override Task OnReminder(string name, CancellationToken cancellationToken)
    {
        Ticks[Id.ToString()] = Context.Caller;
        return Task.CompletedTask;
    }
}

// Owner decision 3 (17 Nov): a reminder or timer tick runs as the scheduler of its brain, never as the kernel; and a call with
// no stamped caller is refused on TestOS as on every host.
public sealed class SchedulerTickFacts
{
    private static readonly BrainId Ada = new("ada");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConfirmationExpiresOnAReminderTickAsTheScheduler()
    {
        await using var os = OS.Local(options => options.CallTimeout = TimeSpan.FromHours(1));
        os.Register<ConfirmerNeuron>().Register<TickWitnessNeuron>();
        var witness = new NeuronId(Ada, "test.tick-witness", "w");
        var confirmer = os.Get<IConfirmer>(new NeuronId(Ada, "test.confirmer", "c"));
        Task<string> asked;
        using (os.Edge.Enter(os.Edge.Person("ada", "ada-account", Ada)))
        {
            await os.Get<ITickWitness>(witness).Arm();
            asked = confirmer.Ask("tick-1", destructive: true);
            for (var i = 0; i < 500 && os.Confirmations.Waiting.All(pending => pending.PlanId != "tick-1"); i++) { await Task.Delay(10, Ct); }
        }

        os.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("Expired:Expired:False", await asked.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await os.Idle();
        var tick = TickWitnessNeuron.Ticks[witness.ToString()];
        Assert.IsType<Actor.Scheduler>(tick?.Actor);
        Assert.Equal(Ada, tick!.Brain);
        Assert.Equal(ConfirmationOutcome.Expired, Assert.Single(os.Confirmations.Audit).Outcome);
    }

    [Fact]
    public async Task UnstampedCallIsRefusedOnTestOS()
    {
        await using var os = OS.Local();
        os.Register<TickWitnessNeuron>();
        var witness = os.Get<ITickWitness>(new NeuronId(Ada, "test.tick-witness", "unstamped"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(witness.Arm);

        using (os.Edge.Enter(os.Edge.Person("ada", "ada-account", Ada)))
        {
            await witness.Arm();
        }
    }
}