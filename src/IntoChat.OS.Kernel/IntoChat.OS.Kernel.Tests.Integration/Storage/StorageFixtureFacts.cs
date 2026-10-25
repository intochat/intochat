using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Kernel.Testing.Storage;

namespace IntoChat.OS.Kernel.Tests.Integration.Storage;

// Brains written by an earlier build stay readable (clean-core W1, decision D2): every record of every fixture brain
// opens under the test seal key and reads with its CLR type, every journalled signal reads with its alias, and every
// grain-state payload reads through the blob storage serializer. Fixtures/{source}/fixture.json records what was written.
public sealed class StorageFixtureFacts(OsHost host)
{
    [Fact]
    public async Task EveryRecordOfTheBdb79fc21FixtureReadsWithItsClrType()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Storage", "Fixtures", StorageFixtureCapture.Source);
        var manifest = StorageFixtures.ReadManifest(fixture);
        var copy = CopyBrainStore(fixture);
        try
        {
            foreach (var brain in manifest.Brains)
            {
                var read = await StorageFixtures.ReadBrainAsync(host.Brain.Services, copy, brain.BrainId, ct);

                Assert.True(read.Unreadable.Length == 0, $"{brain.BrainId}:\n" + string.Join('\n', read.Unreadable));
                Assert.Equal((brain.Format, brain.Snapshot, brain.Records, brain.Signals), (read.Format, read.Snapshot, read.Records.Length, read.Signals.Length));
                Assert.Equal(brain.SignalTypes, SignalTypes(read.Signals));
            }
        }
        finally { Directory.Delete(copy, recursive: true); }

        foreach (var blob in manifest.GrainState)
        {
            var payload = await File.ReadAllBytesAsync(Path.Combine(fixture, StorageFixtures.GrainState, blob.File), ct);
            Assert.True(StorageFixtures.ReadGrainState(host.Brain.Services, blob, payload) is not null, $"{blob.GrainId} {blob.StateName} read as null");
        }
    }

    // The fixture keeps covering what section 4.4 asks for, so the fact above cannot pass on a thinned fixture.
    [Fact]
    public void TheBdb79fc21FixtureCoversBothFormatsASnapshotJournalledSignalsAndGrainState()
    {
        var manifest = StorageFixtures.ReadManifest(Path.Combine(AppContext.BaseDirectory, "Storage", "Fixtures", StorageFixtureCapture.Source));

        Assert.Equal(StorageFixtureCapture.Source, manifest.Source);
        Assert.False(string.IsNullOrWhiteSpace(manifest.CapturedFrom), "a fixture names the exact commit it was captured from");
        Assert.Contains(manifest.Brains, brain => brain.Format == BrainLogFormat.Legacy && !brain.Snapshot);
        Assert.Contains(manifest.Brains, brain => brain.Format == BrainLogFormat.Framed && brain.Snapshot);
        Assert.Contains(manifest.Brains, brain => brain.SignalTypes.Contains("IntoChat.OS.Modules.Time.Timers.Signals.TimerTick"));
        Assert.True(manifest.Brains.Sum(brain => brain.Records) > 0);
        Assert.NotEmpty(manifest.GrainState);
        Assert.DoesNotContain(manifest.Scenarios, scenario => scenario.Contains("FAILED", StringComparison.Ordinal));
    }

    internal static string[] SignalTypes(IEnumerable<Signal> signals)
        => [.. signals.Select(signal => signal.GetType().FullName!).Distinct().Order(StringComparer.Ordinal)];

    // Opening a log claims its writer lock and may truncate a torn tail, so facts read a copy, never the checked-in files.
    internal static string CopyBrainStore(string fixture)
    {
        var copy = Path.Combine(Path.GetTempPath(), "intochat-os-fixture-read-" + Guid.NewGuid().ToString("N"));
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(fixture, StorageFixtures.BrainStore)))
        {
            var target = Path.Combine(copy, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(directory)) { File.Copy(file, Path.Combine(target, Path.GetFileName(file))); }
        }
        return copy;
    }
}
