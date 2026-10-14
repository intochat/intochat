using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class RunHistoryCompatibilityFacts
{
    [Fact]
    public void LegacyMediansSurviveAnEmptyNativeSnapshotAndMatchingHashesExtendTheWindow()
    {
        Dictionary<string, (string Hash, Memory.Sample[] Recent)> legacy = new()
        {
            ["same"] = ("h1", [.. Enumerable.Range(1, 31).Select(i => new Memory.Sample("m" + i, i))]),
            ["legacy-only"] = ("old", [new("legacy", 100)]),
            ["changed"] = ("old", [new("old", 999)])
        };
        Assert.Equal((16L, 31), Memory.Medians(legacy, [])["same"]);
        var result = Memory.Medians(legacy,
        [new("same", "h1", [new("m31", 31), new("m32", 32)]), new("changed", "new", [new("new", 7)])]);
        Assert.Equal((17L, 31), result["same"]);
        Assert.Equal((100L, 1), result["legacy-only"]);
        Assert.Equal((7L, 1), result["changed"]);
        Assert.Equal(31, legacy["same"].Recent.Length);
        Assert.Equal((3L, 2), Memory.Medians(new Dictionary<string, (string, Memory.Sample[])>(),
            [new("even", "hash", [new("a", 9), new("b", 3)])])["even"]);
    }

    [Fact]
    public void HistoryMergesLegacyWithNativeDeduplicatesAndCapsAfterFiltering()
    {
        const string owner = "brain/apps/brainview";
        var legacy = Enumerable.Range(1, 200).Select(i => new BrainView.Run("brain/apps/other/run", "run", "Succeeded", i, "-", "m" + i));
        BrainRunEntry[] native = [new(1, "brain/apps/other", "brain/apps/other/run", RunOutcome.Succeeded, 200, "trace", "m200"),
            new(2, "brain/apps/other", "brain/apps/other/run", RunOutcome.Failed, 0, null, "m201")];
        var merged = BrainView.Merge(legacy.Append(new(owner + "/open", "open", "Succeeded", 1, "-", "own")), native, owner);
        Assert.Equal(200, merged.Length);
        Assert.Equal("m2", merged[0].Mark);
        Assert.Equal("m201", merged[^1].Mark);
        Assert.Equal("trace", merged[^2].Trace);
        Assert.Equal("m192", merged.Reverse().Take(10).Last().Mark);
        Assert.Equal(200, BrainView.Merge(legacy, [], owner).Length);
    }

    [Fact]
    public void HistoryExcludesOnlyItsWholeOwnerAndSkippedRuns()
    {
        const string owner = "brain/apps/brainview";
        BrainView.Run[] legacy = [new(owner + "/open", "open", "Succeeded", 1, "-", "own"),
            new(owner + "-other/open", "open", "Succeeded", 1, "-", "sibling"),
            new("brain/apps/other/run", "run", "Skipped", 0, "-", "skip")];
        BrainRunEntry[] native = [new(1, owner, owner + "/runs", RunOutcome.Succeeded, 1, null, "native-own"),
            new(2, "brain/apps/other", "brain/apps/other/run", RunOutcome.Skipped, 0, null, "native-skip")];
        Assert.Equal("sibling", Assert.Single(BrainView.Merge(legacy, native, owner)).Mark);
    }
}
