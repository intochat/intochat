using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Framework.Tests.Unit;

public sealed class FilteredWatchFacts
{
    [Fact]
    public async Task SourceFiltersSignalsAndRenewalReplacesTheSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var source = brain.Get<IFilteredSource>("filtered-source");
        var selected = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IFilteredObserver>("selected");
        var all = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IFilteredObserver>("all");
        var activation = await source.Watch(selected, [nameof(SelectedSignal)]);
        Assert.Equal(activation, await source.Watch(all));
        await source.Emit();
        Assert.Equal([nameof(SelectedSignal)], await selected.Heard());
        Assert.Equal([nameof(SelectedSignal), nameof(OtherSignal)], await all.Heard());

        Assert.Equal(activation, await source.Watch(selected, [nameof(OtherSignal)]));
        await source.Emit();
        Assert.Equal([nameof(SelectedSignal), nameof(OtherSignal)], await selected.Heard());
        await source.Watch(selected, []);
        await source.Emit();
        Assert.Equal(2, (await selected.Heard()).Length);
        await source.Watch(selected);
        await source.Emit();
        Assert.Equal(4, (await selected.Heard()).Length);
        await source.Unwatch(selected);
        await source.Emit();
        Assert.Equal(4, (await selected.Heard()).Length);
    }
}

[GenerateSerializer] public sealed record SelectedSignal : Signal;
[GenerateSerializer] public sealed record OtherSignal : Signal;
[Alias("test.filtered-source")]
public interface IFilteredSource : INeuron { Task Emit(); }
public interface IFilteredObserver : INeuronObserver, IGrainWithStringKey { Task<string[]> Heard(); }

[GrainType("test-filtered-source")]
public sealed class FilteredSource : Neuron, IFilteredSource
{
    public async Task Emit()
    {
        await PublishAsync(new SelectedSignal());
        await PublishAsync(new OtherSignal());
    }
}

[GrainType("test-filtered-observer")]
public sealed class FilteredObserver : Grain, IFilteredObserver
{
    private readonly List<string> _heard = [];
    public Task<string[]> Heard() => Task.FromResult(_heard.ToArray());
    public Task OnSignalAsync(Signal signal) { _heard.Add(signal.GetType().Name); return Task.CompletedTask; }
}
