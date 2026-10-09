using DigitalBrain.Launcher.Revisions;

namespace DigitalBrain.Launcher.Tests.Unit;

public sealed class RevisionsFileFacts
{
    [Fact]
    public void TheDocumentRoundTripsAndTheNextBlockFollowsTheHighestAllocated()
    {
        var path = Path.Combine(Path.GetTempPath(), "revisions-" + Guid.NewGuid().ToString("N") + ".json");
        var document = new RevisionsDocument("abc1234", [new("abc1234", "os-runtime", @"E:\wt\abc1234", 5090, 5091, "current")]);
        RevisionsFile.Write(path, document);
        var read = RevisionsFile.Read(path);
        Assert.Equal(document.Current, read.Current);
        Assert.Equal(document.Revisions, read.Revisions);
        Assert.Equal("abc1234", read.Current);
        Assert.Equal((5100, 5101), read.NextBlock(firstBlockPort: 5090, blockSize: 10));
        Assert.Equal((5090, 5091), new RevisionsDocument(null, []).NextBlock(5090, 10));
        File.Delete(path);
    }

    [Fact]
    public void AMissingFileIsAnEmptyDocument()
    {
        var read = RevisionsFile.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        Assert.Null(read.Current);
        Assert.Empty(read.Revisions);
    }
}
