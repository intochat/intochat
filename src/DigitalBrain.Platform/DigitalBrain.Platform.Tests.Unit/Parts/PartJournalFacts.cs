using System.Security.Cryptography;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartJournalFacts
{
    [Fact(Timeout = 60_000)]
    public async Task EntriesSurviveReopeningAndATornTailIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log");
        var journal = new PartJournal(path, key, serializer);
        journal.Append(new LogEntry { Seq = 1, GrainId = "grain-in-clear", StateName = "s", Change = RecordChange.Write, Record = [1], Signals = [] });
        journal.Append(new LogEntry { Seq = 2, GrainId = "grain-in-clear", StateName = "s", Change = RecordChange.Write, Record = [2], Signals = [] });
        await File.AppendAllBytesAsync(path, [0, 0, 1, 0, 42], ct);

        var entries = new PartJournal(path, key, serializer).Read();

        Assert.Equal([1L, 2L], entries.Select(e => e.Seq));
        Assert.DoesNotContain("grain-in-clear", await File.ReadAllTextAsync(path, ct), StringComparison.Ordinal);
    }

    [Fact(Timeout = 60_000)]
    public async Task TrimmingDropsCommittedEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var journal = new PartJournal(Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log"), RandomNumberGenerator.GetBytes(32), brain.Services.GetRequiredService<Serializer>());
        foreach (var seq in new long[] { 1, 2, 3 }) { journal.Append(new LogEntry { Seq = seq, GrainId = "g", StateName = "s", Record = [], Signals = [] }); }

        journal.TrimThrough(2);

        Assert.Equal([3L], journal.Read().Select(e => e.Seq));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAppendThatFailsMidwayLeavesNoBytesBehindTheNextOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log");
        var journal = new PartJournal(path, key, serializer) { Interrupt = () => throw new IOException("The disk went away mid-record.") };
        Assert.Throws<IOException>(() => journal.Append(new LogEntry { Seq = 1, GrainId = "g", StateName = "s", Record = [1] }));
        journal.Interrupt = null;

        journal.Append(new LogEntry { Seq = 1, GrainId = "g", StateName = "s", Record = [2] });

        Assert.Equal([2], Assert.Single(new PartJournal(path, key, serializer).Read()).Record);
    }

    [Fact(Timeout = 60_000)]
    public async Task AJournalLargerThanTheSingleReadLimitIsReadEntryByEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log");
        var journal = new PartJournal(path, key, serializer);
        for (var seq = 1L; seq <= 20; seq++) { journal.Append(new LogEntry { Seq = seq, GrainId = "g", StateName = "s", Record = new byte[100], Signals = [] }); }
        const int limit = 16;
        Assert.True(new FileInfo(path).Length > 100 * limit);

        var entries = new PartJournal(path, key, serializer) { SingleReadLimit = limit }.Read();

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), entries.Select(e => e.Seq));
    }

    [Fact(Timeout = 60_000)]
    public async Task EntriesBehindAnAcceptedHeadAreDroppedFromTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.log");
        var journal = new PartJournal(path, key, serializer) { SingleReadLimit = 16 };
        foreach (var seq in new long[] { 1, 2, 3 }) { journal.Append(new LogEntry { Seq = seq, GrainId = "g", StateName = "s", Record = new byte[100], Signals = [] }); }
        var full = new FileInfo(path).Length;

        journal.TrimThrough(2);
        var trimmed = new FileInfo(path).Length;
        journal.Append(new LogEntry { Seq = 4, GrainId = "g", StateName = "s", Record = [4], Signals = [] });

        Assert.True(trimmed < full / 2);
        Assert.Equal([3L, 4L], new PartJournal(path, key, serializer).Read().Select(e => e.Seq));
        new PartJournal(path, key, serializer).TrimThrough(4);
        Assert.Equal(0, new FileInfo(path).Length);
    }
}
