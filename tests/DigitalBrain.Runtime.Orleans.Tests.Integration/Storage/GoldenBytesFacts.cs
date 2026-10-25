using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Storage;

/// <summary>
/// Pins the persisted bytes of the BrainLog <c>Json</c> format (04-05, SAFE-01 clean break): the segment header and the Orleans
/// framing of an entry and a snapshot whose signal and state payloads are JSON. A change here is a format change.
/// </summary>
public sealed class GoldenBytesFacts
{
    internal const string SegmentHeader = "44424C4702";
    internal const string Entry =
        "20E8001D412B6E6575726F6E2E686F73742F62312F636861742F70410B737461746509030541437B227469746C65223A226D222C22636F756E74223A312C2274616773223A5B5D7D411362312F636861742F7021000321E84013746573742E6E6F7465411B7B2274657874223A226869227DE0E0010D012D012000B22DB8D0E0";

    internal const string Snapshot =
        "20E8001D012D213001312CD42E1D537472696E67436F6D706172657200030101E00103412B6E6575726F6E2E686F73742F62312F636861742F7020380300030101E00103410B737461746520E8400F7B226E223A317D011DE0E0E021000321E8001D4101C12F0903014101411362312F636861742F7021000321E84013746573742E6E6F7465411B7B2274657874223A226869227DE0E0010D012D012000B22DB8D0E0E021000321E8C0370109010DE0E0E0";

    private static Serializer Serializer()
        => new ServiceCollection().AddSerializer(s => s.AddAssembly(typeof(BrainLog).Assembly)).BuildServiceProvider().GetRequiredService<Serializer>();

    private static readonly JournaledSignalPayload NotePayload = new("test.note", """{"text":"hi"}"""u8.ToArray());

    internal static BrainEntry EntryFixture() => new(
        7, "neuron.host/b1/chat/p", "state", BrainRecordChange.Write, """{"title":"m","count":1,"tags":[]}"""u8.ToArray(),
        "b1/chat/p", [NotePayload], 3, 11, 1_792_886_400_000);

    internal static BrainSnapshot SnapshotFixture() => new(
        7, 11,
        new(StringComparer.Ordinal) { ["neuron.host/b1/chat/p"] = new(StringComparer.Ordinal) { ["state"] = new("""{"n":1}"""u8.ToArray(), 7) } },
        [EntryFixture() with { GrainId = "", StateName = "", Record = [], Change = BrainRecordChange.None }],
        [new PublisherMark("b1/chat/p", 2, 3)]);

    [Fact]
    public void The_segment_header_is_DBLG_and_format_Json()
    {
        Assert.Equal(2, (byte)BrainLogFormat.Json);
        Assert.Equal(SegmentHeader, Convert.ToHexString([.. "DBLG"u8, (byte)BrainLogFormat.Json]));
    }

    [Fact]
    public void A_BrainEntry_with_a_json_signal_payload_has_pinned_bytes()
    {
        var serializer = Serializer();
        Assert.Equal(Entry, Convert.ToHexString(serializer.SerializeToArray(EntryFixture())));
        var read = serializer.Deserialize<BrainEntry>(Convert.FromHexString(Entry))!;
        Assert.Equal("test.note", read.Signals[0].Type);
        Assert.Equal(NotePayload.Json, read.Signals[0].Json);
        Assert.Equal((7L, 3L, 11L, 1_792_886_400_000L), (read.Sequence, read.FirstSignal, read.FirstPosition, read.AtUnixMs));
    }

    [Fact]
    public void A_BrainSnapshot_with_publisher_floors_has_pinned_bytes()
    {
        var serializer = Serializer();
        Assert.Equal(Snapshot, Convert.ToHexString(serializer.SerializeToArray(SnapshotFixture())));
        var read = serializer.Deserialize<BrainSnapshot>(Convert.FromHexString(Snapshot))!;
        Assert.Equal(new PublisherMark("b1/chat/p", 2, 3), read.Publishers[0]);
        Assert.Equal(7, read.Records["neuron.host/b1/chat/p"]["state"].ETag);
        Assert.Equal("b1/chat/p", read.Signals[0].Publisher);
    }
}
