using System.Buffers.Binary;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Integrations;
using IntoChat.OS.Testing;
using IntoChat.OS.Time.Timers.Signals;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

// Stored bytes are Orleans binary; these pin today's layout per contract shape, per storage envelope and per sealed
// segment format, captured from the Orleans-attributed build (clean-core W1, section 6). A codec that moves to a
// generator must write exactly these bytes and read them back. A mismatch writes the actual hex to golden/ beside
// the test binary. Sealed segments carry a random nonce, so they are checked in once and read, not rewritten.
public sealed class GoldenBytesFacts : IDisposable
{
    private const string BrainId = "brain-1";
    private const string TimerGrain = "timer/brain-1/timer-1";

    private static readonly ServiceProvider Services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly Serializer Serializer = Services.GetRequiredService<Serializer>();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "intochat-os-golden-" + Guid.NewGuid().ToString("N"));

    private static TimerTick Tick() => new("timer-1", new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(3)))
    {
        Publisher = "brain-1/timer-1",
        Trace = Trace(),
    };

    private static Trace Trace() => new() { TraceId = "trace-1", PayerAccountId = "acct-1", BrainId = BrainId, Origin = "brain-1/clock", BudgetMicros = 42 };

    private static Signal BaseSignal() => new() { Publisher = "brain-1/clock", Trace = Trace() };

    private static BrainEntry Entry()
    {
        var tick = Tick();
        return new BrainEntry(7, TimerGrain, "state", BrainRecordChange.Write, [1, 2, 3], [tick, tick, new Signal { Publisher = "brain-1/clock" }], 5);
    }

    private static BrainSnapshot Snapshot()
    {
        var entry = Entry();
        var records = new Dictionary<string, Dictionary<string, BrainRecord>> { [TimerGrain] = new() { ["state"] = new BrainRecord([4, 5], 3) } };
        return new BrainSnapshot(9, 8, records, [entry, entry], BrainLogFormat.Framed);
    }

    [Fact]
    public void ASealedRecordKeepsItsBytes()
    {
        Matches(nameof(Golden.SealedRecord), Golden.SealedRecord, Serializer.SerializeToArray(Trace()));
        Assert.Equal(Trace(), Serializer.Deserialize<Trace>(Convert.FromHexString(Golden.SealedRecord)));
    }

    [Fact]
    public void ARecordStructKeepsItsBytes()
    {
        NeuronId id = "brain-1/timer-1";
        Matches(nameof(Golden.RecordStruct), Golden.RecordStruct, Serializer.SerializeToArray(id));
        Assert.Equal(id, Serializer.Deserialize<NeuronId>(Convert.FromHexString(Golden.RecordStruct)));
    }

    [Fact]
    public void AClassKeepsItsBytes()
    {
        var state = new ConnectionRequestsState { Pending = [new ConnectionRef("gmail", "brain-1/gmail-1")], Revision = 7 };
        Matches(nameof(Golden.Class), Golden.Class, Serializer.SerializeToArray(state));
        var read = Serializer.Deserialize<ConnectionRequestsState>(Convert.FromHexString(Golden.Class))!;
        Assert.Equal(state.Pending, read.Pending);
        Assert.Equal(7, read.Revision);
    }

    [Fact]
    public void TheBaseSignalKeepsItsBytes()
    {
        Matches(nameof(Golden.BaseSignal), Golden.BaseSignal, Serializer.SerializeToArray(BaseSignal()));
        Assert.Equal(BaseSignal(), Serializer.Deserialize<Signal>(Convert.FromHexString(Golden.BaseSignal)));
    }

    [Fact]
    public void ASignalKeepsItsExactBytes()
    {
        Matches(nameof(Golden.TimerTick), Golden.TimerTick, Serializer.SerializeToArray(Tick()));
        Assert.Equal(Tick(), Serializer.Deserialize<TimerTick>(Convert.FromHexString(Golden.TimerTick)));
    }

    [Fact]
    public void APolymorphicSignalCarriesItsAliasAndKeepsItsBytes()
    {
        var bytes = Serializer.SerializeToArray<Signal>(Tick());

        Matches(nameof(Golden.TimerTickAsSignal), Golden.TimerTickAsSignal, bytes);
        Assert.True(bytes.AsSpan().IndexOf("time.timer-tick"u8) >= 0, "the alias is on the wire");
        Assert.Equal(Tick(), Serializer.Deserialize<Signal>(Convert.FromHexString(Golden.TimerTickAsSignal)));
    }

    [Fact]
    public void ASharedReferenceIsWrittenOnceAndReadBackShared()
    {
        var tick = Tick();
        Matches(nameof(Golden.SignalArrayWithSharedTick), Golden.SignalArrayWithSharedTick, Serializer.SerializeToArray<Signal[]>([tick, tick]));

        var read = Serializer.Deserialize<Signal[]>(Convert.FromHexString(Golden.SignalArrayWithSharedTick))!;
        Assert.Equal(Tick(), read[0]);
        Assert.Same(read[0], read[1]);
    }

    [Fact]
    public void TheStorageEnvelopesKeepTheirBytes()
    {
        Matches(nameof(Golden.BrainRecord), Golden.BrainRecord, Serializer.SerializeToArray(new BrainRecord([4, 5], 3)));
        Matches(nameof(Golden.BrainEntry), Golden.BrainEntry, Serializer.SerializeToArray(Entry()));
        Matches(nameof(Golden.BrainSnapshot), Golden.BrainSnapshot, Serializer.SerializeToArray(Snapshot()));
    }

    [Fact]
    public void TheStorageEnvelopesReadBack()
    {
        var snapshot = Serializer.Deserialize<BrainSnapshot>(Convert.FromHexString(Golden.BrainSnapshot))!;

        var entry = snapshot.Signals[0];
        Assert.Same(entry, snapshot.Signals[1]);
        Assert.Equal(Entry().Signals, entry.Signals);
        Assert.Same(entry.Signals[0], entry.Signals[1]);
        Assert.IsType<TimerTick>(entry.Signals[0]);
        Assert.Equal(typeof(Signal), entry.Signals[2].GetType());
        Assert.Equal(BrainLogFormat.Framed, snapshot.Format);
        Assert.Equal([4, 5], snapshot.Records[TimerGrain]["state"].Record);
        Assert.Equal(3, snapshot.Records[TimerGrain]["state"].ETag);
    }

    [Theory]
    [InlineData(BrainLogFormat.Legacy)]
    [InlineData(BrainLogFormat.Framed)]
    public async Task ASealedSegmentOpensWithTheTestSealKey(BrainLogFormat format)
    {
        var segment = Convert.FromHexString(format == BrainLogFormat.Legacy ? Golden.LegacySegment : Golden.FramedSegment);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "initial.log"), segment);

        // The framing: an optional "DBLG" + version header, then length-prefixed frames sealed under the segment context.
        var offset = format == BrainLogFormat.Framed ? 5 : 0;
        Assert.Equal(format == BrainLogFormat.Framed, segment.AsSpan().StartsWith("DBLG\u0001"u8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(offset));
        var plain = SegmentSeal.Open(TestSealKey.Bytes(), $"brain-store|{BrainId}|initial|log", segment.AsSpan(offset + 4, length).ToArray());
        Matches(nameof(Golden.SegmentEntry), Golden.SegmentEntry, plain);

        await using var log = new BrainLog(_directory, BrainId, TestSealKey.Bytes(), Serializer, format);
        Assert.Equal(format, log.Format);
        var record = await log.Read(TimerGrain, "state");
        Assert.NotNull(record);
        Assert.Equal([1, 2, 3], record.Record);
        Assert.Equal(1, record.ETag);
        var signals = await log.ReadSignals(0, 10, TimerGrain, TestContext.Current.CancellationToken);
        Assert.Equal([Tick(), Tick(), new Signal { Publisher = "brain-1/clock" }], signals.Select(signal => signal.Signal));
    }

    // Writes a fresh sealed segment per format (a new nonce each time) to golden/ beside the test binary.
    [Fact(Explicit = true)]
    public async Task CaptureSealedSegments()
    {
        foreach (var format in new[] { BrainLogFormat.Legacy, BrainLogFormat.Framed })
        {
            var directory = Path.Combine(_directory, format.ToString());
            await using (var log = new BrainLog(directory, BrainId, TestSealKey.Bytes(), Serializer, format))
            {
                var tick = Tick();
                await log.Append(TimerGrain, "state", null, BrainRecordChange.Write, [1, 2, 3], [tick, tick, new Signal { Publisher = "brain-1/clock" }], TestContext.Current.CancellationToken);
            }
            Write(format + "Segment", await File.ReadAllBytesAsync(Path.Combine(directory, "initial.log"), TestContext.Current.CancellationToken));
        }
    }

    private static void Matches(string name, string golden, byte[] actual)
    {
        var hex = Convert.ToHexString(actual);
        if (!string.Equals(golden, hex, StringComparison.Ordinal)) { Write(name, actual); }
        Assert.Equal(golden, hex);
    }

    private static void Write(string name, byte[] bytes)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "golden");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".hex"), Convert.ToHexString(bytes));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
