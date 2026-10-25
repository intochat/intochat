using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

// The source nudges a watcher only for the signals it selected; each observer here records the signal at the
// nudged sequence, so what it hears is exactly what the source chose to nudge it about.
public sealed class FilteredWatchFacts(FrameworkHost host)
{
    [Fact]
    public async Task SourceFiltersSignalsAndRenewalReplacesTheSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<IFilteredSource>("filtered-source");
        var selected = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IFilteredObserver>("selected");
        var all = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IFilteredObserver>("all");
        Task<string[]> Heard(IFilteredObserver observer, int count)
            => TestWait.UntilAsync(_ => observer.Heard(), heard => heard.Length >= count, TimeSpan.FromSeconds(15), ct);

        var activation = await source.Watch(selected, [nameof(SelectedSignal)]);
        Assert.Equal(activation, await source.Watch(all));
        await source.Emit();
        Assert.Equal([nameof(SelectedSignal), nameof(OtherSignal)], await Heard(all, 2));
        Assert.Equal([nameof(SelectedSignal)], await Heard(selected, 1));

        Assert.Equal(activation, await source.Watch(selected, [nameof(OtherSignal)]));
        await source.Emit();
        await Heard(all, 4);
        Assert.Equal([nameof(SelectedSignal), nameof(OtherSignal)], await Heard(selected, 2));
        await source.Watch(selected, []);
        await source.Emit();
        await Heard(all, 6);
        Assert.Equal(2, (await selected.Heard()).Length);
        await source.Watch(selected);
        await source.Emit();
        await Heard(all, 8);
        Assert.Equal(4, (await Heard(selected, 4)).Length);
        await source.Unwatch(selected);
        await source.Emit();
        await Heard(all, 10);
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
    // One-way nudges may arrive out of order; what was heard is kept in journal order.
    private readonly SortedList<long, string> _heard = [];
    public Task<string[]> Heard() => Task.FromResult(_heard.Values.ToArray());
    public async Task Nudge(SignalNudge nudge)
    {
        var page = await NudgeReader.Source(GrainFactory, nudge).Read(nudge.Sequence - 1, NudgeReader.PageSize);
        var index = Array.IndexOf(page.Sequences, nudge.Sequence);
        if (index >= 0) { _heard[nudge.Sequence] = page.Signals[index].GetType().Name; }
    }
}
