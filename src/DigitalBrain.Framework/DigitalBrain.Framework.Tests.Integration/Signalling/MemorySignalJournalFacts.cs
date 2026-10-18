using Microsoft.Extensions.Options;

namespace DigitalBrain.Framework.Tests.Integration.Signalling;

// A host without a part log still journals: one bounded ring per brain, sequenced per brain.
public sealed class MemorySignalJournalFacts
{
    [Fact]
    public async Task SequencesAreMonotonicPerBrainAndSignalsOfFiltersByPublisher()
    {
        var journal = new MemorySignalJournal(Options.Create(new MemorySignalJournalOptions()));
        var first = GrainId.Create("test", "brain-a/first");
        var second = GrainId.Create("test", "brain-a/second");
        var other = GrainId.Create("test", "brain-b/first");

        Assert.Equal(2, await journal.Append(first, [new SelectedSignal(), new OtherSignal()]));
        Assert.Equal(3, await journal.Append(second, [new SelectedSignal()]));
        Assert.Equal(1, await journal.Append(other, [new SelectedSignal()]));
        Assert.Equal(4, await journal.Append(first, [new OtherSignal()]));

        Assert.Equal([1L, 2L, 3L, 4L], (await journal.ReadSince("brain-a", 0, 100)).Select(item => item.Sequence));
        Assert.Equal([3L], (await journal.ReadSince("brain-a", 2, 1)).Select(item => item.Sequence));
        var part = await journal.SignalsOf(first, 1);
        Assert.NotNull(part);
        Assert.Equal(0, part.Floor);
        Assert.Equal(4, part.Last);
        Assert.Equal([2L, 4L], part.Signals.Select(item => item.Sequence));
        Assert.IsType<OtherSignal>(part.Signals[1].Signal);
    }

    [Fact]
    public async Task TheRingDropsTheOldestAndReportsTheFloor()
    {
        var journal = new MemorySignalJournal(Options.Create(new MemorySignalJournalOptions { Retain = 2 }));
        var publisher = GrainId.Create("test", "brain-a/first");

        await journal.Append(publisher, [new SelectedSignal(), new SelectedSignal(), new SelectedSignal(), new OtherSignal()]);

        var part = await journal.SignalsOf(publisher, 0);
        Assert.NotNull(part);
        Assert.Equal(2, part.Floor);
        Assert.True(part.Floor > 0);
        Assert.Equal(4, part.Last);
        Assert.Equal([3L, 4L], part.Signals.Select(item => item.Sequence));
        Assert.Equal([3L, 4L], (await journal.ReadSince("brain-a", 0, 100)).Select(item => item.Sequence));
    }
}
