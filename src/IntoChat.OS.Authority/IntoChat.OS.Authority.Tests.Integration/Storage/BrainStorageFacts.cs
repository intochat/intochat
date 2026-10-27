using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

// A plain neuron (06-08) whose state and signal ride in the brain's log.
[DigitalBrain.Contracts.NeuronAlias("test.local-counter"), DigitalBrain.Contracts.DefaultNeuronType(LocalCounter.Type)]
public interface ILocalCounter { Task Set(int value); Task<int> Read(); }

public sealed class LocalCounterState { public int Value { get; set; } }

[DigitalBrain.Signal("test.local-counter.changed")]
public sealed record LocalChanged(int Value) : DigitalBrain.Signal;

[DigitalBrain.Neuron(Type)]
public sealed class LocalCounter : DigitalBrain.Neuron<LocalCounterState>, ILocalCounter
{
    public const string Type = "test.local-counter";

    public Task Set(int value) => Save(new LocalCounterState { Value = value }, new LocalChanged(value));
    public Task<int> Read() => Task.FromResult(State.Value);
}

[DigitalBrain.Module("test.local-counter")]
public sealed class LocalCounterNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

[Trait("host", "own")]
public sealed class BrainStorageFacts
{
    [Fact]
    public async Task LocalAccountsDoNotRegisterTheRuntimeAsAMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var identity = brain.Services.GetRequiredService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>();
        var person = await brain.PersonAsync("local-user", ct);
        Assert.NotNull(person);
        Assert.NotNull(await identity.ReadAccount("owner"));
    }

    [Fact]
    public async Task TheProductionRecipePersistsStateAndSignalsWithoutAMachineRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<LocalCounterNeurons>().StartAsync(ct);
        Assert.NotNull(brain.Services.GetService<DigitalBrain.Runtime.Orleans.IBrainJournalWriter>());
        using (brain.Owner.AsCaller()) { await brain.Get<ILocalCounter>("owner/apps/counter").Set(17); }
        Assert.Equal([17], (await brain.History<LocalChanged>(brain.Get<ILocalCounter>("owner/apps/counter"), ct)).Select(changed => changed.Value));
        await brain.RestartAsync(ct);
        using (brain.Owner.AsCaller()) { Assert.Equal(17, await brain.Get<ILocalCounter>("owner/apps/counter").Read()); }
        Assert.Equal([17], (await brain.History<LocalChanged>(brain.Get<ILocalCounter>("owner/apps/counter"), ct)).Select(changed => changed.Value));
    }
}
