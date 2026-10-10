using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class SignalsReadFacts
{
    private static string Scope => BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;

    private static string Counter => $"{Scope}/apps/counter/s1";

    [Fact(Timeout = 60_000)]
    public async Task TheJournalReadsBackTheBrainsSignalsInOrderAndPagesFromACursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);
        await counter.Ping();
        await counter.Change(2);
        await (await brain.SiloServices.GetRequiredService<PartLogs>().For(PartId.Of(Counter))).Flushed();
        var journal = brain.SiloServices.GetRequiredService<ISignalJournal>();

        var all = await SignalsEndpoints.Read(journal, Scope, 0, 100);
        var counterSignals = all.Signals.Where(s => s.Publisher.Contains("counter")).ToArray();
        var paged = await SignalsEndpoints.Read(journal, Scope, counterSignals[0].Sequence, 100);

        Assert.Equal(["Changed", "Pinged", "Changed"], counterSignals.Select(s => s.Type));
        Assert.Equal(2, counterSignals[2].Payload.GetProperty("Value").GetInt32());
        Assert.Equal(all.Signals.Select(s => s.Sequence).Order(), all.Signals.Select(s => s.Sequence));
        Assert.Equal(all.Next, all.Signals[^1].Sequence);
        Assert.Equal(all.Signals.Skip(all.Signals.ToList().IndexOf(counterSignals[0]) + 1).Select(s => s.Sequence), paged.Signals.Select(s => s.Sequence));
        Assert.Single(await SignalsEndpoints.Read(journal, Scope, 0, 1) is { } one ? one.Signals : []);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReadPastTheEndIsAnEmptyPageAtTheSameCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        await brain.Get<ISignalCounter>(Counter).Change(1);
        var journal = brain.SiloServices.GetRequiredService<ISignalJournal>();

        var page = await SignalsEndpoints.Read(journal, Scope, 99, 10);

        Assert.Empty(page.Signals);
        Assert.Equal(99, page.Next);
    }

    [Fact(Timeout = 60_000)]
    public async Task APlainHostReturnsAnEmptyPageWithNextEqualToSince()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var journal = brain.SiloServices.GetRequiredService<ISignalJournal>();

        var page = await SignalsEndpoints.Read(journal, Scope, 7, 10);

        Assert.Empty(page.Signals);
        Assert.Equal(7, page.Next);
    }
}
