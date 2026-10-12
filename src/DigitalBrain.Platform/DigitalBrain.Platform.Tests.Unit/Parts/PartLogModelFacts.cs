using DigitalBrain.Framework.Mesh;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartLogModelFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AnEntryCarriesItsRecordAndItsSignalsTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var segment = new LogSegment
        {
            FirstSeq = 5, Prev = new MeshRef("p/log/1", "sha256:aa", ["n0"]),
            Entries = [new LogEntry { Seq = 5, GrainId = "g", StateName = "s", Change = RecordChange.Write, Record = [1], Signals = [new Probed("x")] }],
        };

        var back = serializer.Deserialize<LogSegment>(serializer.SerializeToArray(segment))!;

        Assert.Equal((5L, "p/log/1", RecordChange.Write), (back.FirstSeq, back.Prev!.SegmentId, back.Entries[0].Change));
        Assert.Equal(new byte[] { 1 }, back.Entries[0].Record);
        Assert.Equal("x", Assert.IsType<Probed>(Assert.Single(back.Entries[0].Signals)).Value);
    }

    [Fact(Timeout = 60_000)]
    public async Task AHeadPayloadAndASnapshotRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var head = new HeadPayload { Snapshot = new MeshRef("p/snapshot/9", "sha256:bb", ["n1"]), SnapshotSeq = 9, LastSeq = 12, SegmentsSinceSnapshot = 2 };
        var index = new PartSnapshotIndex { Seq = 9, SignalsFrom = 8, RecordChunks = [new MeshRef("p/snapshot/9/records/0", "sha256:cc", ["n2"])] };
        var chunk = new PartRecordChunk { Records = [new SnapshotRecord { GrainId = "g", StateName = "s", Record = [2], ETagSeq = 7 }] };

        var headBack = serializer.Deserialize<HeadPayload>(serializer.SerializeToArray(head))!;
        var indexBack = serializer.Deserialize<PartSnapshotIndex>(serializer.SerializeToArray(index))!;
        var chunkBack = serializer.Deserialize<PartRecordChunk>(serializer.SerializeToArray(chunk))!;

        Assert.Equal((9L, 12L, 2), (headBack.SnapshotSeq, headBack.LastSeq, headBack.SegmentsSinceSnapshot));
        Assert.Equal(8L, indexBack.SignalsFrom);
        Assert.Equal("p/snapshot/9/records/0", Assert.Single(indexBack.RecordChunks).SegmentId);
        Assert.Equal(7L, Assert.Single(chunkBack.Records).ETagSeq);
    }
}

[GenerateSerializer, Alias("test.probed")]
public sealed record Probed([property: Id(0)] string Value) : Signal;
