using DigitalBrain.Hosting;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Conformance = DigitalBrain.Testing.Conformance.Host;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Hosting;

public sealed record KeeperState
{
    public string[] Heard { get; init; } = [];
}

public interface IKeeper
{
    Task FollowNumbers(string source);

    Task FollowCounter(string source);

    Task Unfollow(string source);

    Task<string[]> Heard();
}

/// <summary>A subscriber that stays hosted while the class of one of its publishers is removed from the host.</summary>
[Neuron("probe.keeper", State = "heard")]
public sealed class KeeperNeuron : Neuron<KeeperState>, IKeeper
{
    public Task FollowNumbers(string source) => Context.Wires.Ensure<Conformance.Number>(NeuronId.Parse(source));

    public Task FollowCounter(string source) => Context.Wires.Ensure<Counted>(NeuronId.Parse(source));

    public Task Unfollow(string source) => Context.Wires.Remove(NeuronId.Parse(source));

    public Task<string[]> Heard() => Task.FromResult(State.Heard);

    [On]
    public Task On(Conformance.Number signal) => Record("number:" + signal.Value);

    [On]
    public Task On(Counted signal) => Record("counted:" + signal.Value);

    private Task Record(string item) => Save(State with { Heard = [.. State.Heard, item] }, new Noted(item));
}

// What a brain goes through when a release deletes a neuron class: the brain still holds that type's stored state, its journaled
// signals and the wires other neurons hold on it. The first silo hosts the type (e2e.source, from the conformance assembly), the
// second silo opens the same Home without that assembly. Unknown-alias handling exists only in the Orleans storage, so the silo
// fact is the one that describes a real brain; the TestOS fact states what TestOS can and cannot show.
public sealed class RemovedNeuronTypeFacts
{
    private const string Removed = "b1/e2e.source/gone";
    private const string Keeper = "b1/probe.keeper/stays";
    private const string Counter = "b1/probe.counter/stays";

    [Fact]
    public async Task A_brain_with_state_signals_and_a_wire_of_a_removed_type_keeps_working_on_a_silo_that_no_longer_hosts_it()
    {
        using var platform = TestStamp.Kernel();
        var ct = TestContext.Current.CancellationToken;
        using var home = new TempHome();

        await using (var before = await Start(home.Path, [typeof(HostingModule), typeof(Conformance.FrameworkModule)], ct))
        {
            var keeper = NeuronHosting.Get<IKeeper>(before.Services, NeuronId.Parse(Keeper));
            await keeper.FollowNumbers(Removed);
            await keeper.FollowCounter(Counter);
            var source = NeuronHosting.Get<Conformance.ISource>(before.Services, NeuronId.Parse(Removed));
            await source.Emit(7);
            await source.Emit(8);
            await HeardUntil(() => NeuronHosting.Get<IKeeper>(before.Services, NeuronId.Parse(Keeper)).Heard(), 2, ct);
            Assert.Equal(["number:7", "number:8"], await keeper.Heard());
            Assert.Equal(2, await source.Count());
            Assert.NotNull(await (await Logs(before).For("b1")).Read("neuron.host/" + Removed, "source"));
        }

        await using var after = await Start(home.Path, [typeof(HostingModule)], ct);
        var types = after.Services.GetRequiredService<NeuronTypeRegistry>();
        Assert.False(types.IsHosted("e2e.source"));
        Assert.True(types.IsHosted("probe.keeper"));

        // 1. The subscriber of the removed type activates with its state and its wires.
        var kept = NeuronHosting.Get<IKeeper>(after.Services, NeuronId.Parse(Keeper));
        Assert.Equal(["number:7", "number:8"], await kept.Heard());

        // 2. It still hears a publisher that is hosted: the dead wire does not stall catch-up.
        Assert.Equal(1, await NeuronHosting.Get<ICounter>(after.Services, NeuronId.Parse(Counter)).Add(1));
        await HeardUntil(kept.Heard, 3, ct);
        Assert.Equal(["number:7", "number:8", "counted:1"], await kept.Heard());

        // 3. A call to the removed type fails with a binding error that names it; nothing else is touched.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => NeuronHosting.Get<Conformance.ISource>(after.Services, NeuronId.Parse(Removed)).Count());
        Assert.Contains("e2e.source", error.ToString(), StringComparison.Ordinal);

        // 4. Signal history reads without throwing. The removed publisher's signals have an alias the host no longer knows:
        //    they are skipped, not decoded and not an error.
        var history = after.Services.GetRequiredService<BrainSignalHistories>().For(new BrainId("b1"));
        Assert.Empty((await history.Read(NeuronId.Parse(Removed), 0, 100, ct)).Signals);
        var brainWide = await history.Read(0, 100, ct);
        Assert.Contains(brainWide.Signals, signal => signal.Signal is Counted);
        Assert.DoesNotContain(brainWide.Signals, signal => signal.Publisher == NeuronId.Parse(Removed));

        // 5. Nothing cleans up: the removed type's state record and the wire on it stay in the brain (a leak, not a failure).
        var log = await Logs(after).For("b1");
        Assert.NotNull(await log.Read("neuron.host/" + Removed, "source"));
        Assert.Contains(log.Subscriptions.Of(NeuronId.Parse(Keeper))!.Entries, entry => entry.Source == NeuronId.Parse(Removed));

        // 6. The wire can be removed by id alone, without the type.
        await kept.Unfollow(Removed);
        Assert.DoesNotContain(log.Subscriptions.Of(NeuronId.Parse(Keeper))!.Entries, entry => entry.Source == NeuronId.Parse(Removed));
        Assert.Equal(2, await NeuronHosting.Get<ICounter>(after.Services, NeuronId.Parse(Counter)).Add(1));
        await HeardUntil(kept.Heard, 4, ct);
        Assert.Equal(["number:7", "number:8", "counted:1", "counted:2"], await kept.Heard());
    }

    [Fact]
    public async Task On_TestOS_stored_state_of_a_type_that_is_not_registered_is_inert_and_a_wire_on_it_cannot_be_made()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await JourneyOS.Start(NeuronRuntimeKind.TestOS, [typeof(HostingModule)], ct: ct);
        var ada = os.Person("ada");
        var removed = new NeuronId(ada.Brain, "e2e.source", "gone");
        var keeperId = new NeuronId(ada.Brain, "probe.keeper", "stays");
        var counterId = new NeuronId(ada.Brain, "probe.counter", "stays");
        await os.Runtime.SeedState(removed, """{"count":2}""", ct);

        var keeper = ada.Get<IKeeper>(keeperId);
        await keeper.FollowCounter(counterId.ToString());
        Assert.Equal(1, await ada.Get<ICounter>(counterId).Add(1));
        await os.Idle();
        Assert.Equal(["counted:1"], await keeper.Heard());

        // TestOS has no unknown-type path: a call and a new wire are both refused before any class is looked up (a person's call
        // fails the owner check), so an old wire on a removed type cannot be reproduced here. The silo fact above covers it.
        await Assert.ThrowsAnyAsync<Exception>(() => ada.Get<Conformance.ISource>(removed).Count());
        await Assert.ThrowsAnyAsync<Exception>(() => keeper.FollowNumbers(removed.ToString()));

        Assert.Equal(2, await ada.Get<ICounter>(counterId).Add(1));
        await os.Idle();
        Assert.Equal(["counted:1", "counted:2"], await keeper.Heard());
    }

    private static Task<IInProcessHost> Start(string home, IReadOnlyList<Type> modules, CancellationToken ct)
    {
        var assemblies = modules.Select(module => module.Assembly).Distinct().ToArray();
        return new LocalSiloHostFactory(new BrainStorageRegistration(assemblies)).Start(new InProcessHostOptions { Home = home, Modules = modules }, ct);
    }

    private static BrainLogs Logs(IInProcessHost host) => host.Services.GetRequiredService<BrainLogs>();

    private static async Task HeardUntil(Func<Task<string[]>> heard, int count, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        while ((await heard()).Length < count) { await Task.Delay(50, limit.Token); }
    }
}
