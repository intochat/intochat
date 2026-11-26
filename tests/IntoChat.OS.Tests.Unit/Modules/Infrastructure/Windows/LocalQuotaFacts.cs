using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// D-11, CAP-04: a Windows PC's quota sums the distinct volumes of its allowed roots, used = TotalSize - AvailableFreeSpace; anything
// uncertain is Unknown, never 0 and never an exception.
public sealed class LocalQuotaFacts
{
    [Fact]
    public void OneRootOnAKnownVolumeIsKnownUsedAndTotal()
    {
        var quota = LocalVolumes.QuotaFor([@"C:\data"], new FakeVolumeSource(new() { [@"C:\"] = Row(@"C:\", true, 100, 40) }));
        Assert.Equal((QuotaState.Known, 60L, 100L), (quota.State, quota.Used, quota.Total));
    }

    [Fact]
    public void TwoRootsOnOneVolumeCountItOnce()
    {
        var quota = LocalVolumes.QuotaFor([@"C:\a", @"c:\b"], new FakeVolumeSource(new() { [@"C:\"] = Row(@"C:\", true, 100, 40) }));
        Assert.Equal((60L, 100L), (quota.Used!.Value, quota.Total!.Value));
    }

    [Fact]
    public void TwoVolumesSum()
    {
        var quota = LocalVolumes.QuotaFor([@"C:\a", @"D:\b"], new FakeVolumeSource(new() { [@"C:\"] = Row(@"C:\", true, 100, 40), [@"D:\"] = Row(@"D:\", true, 50, 10) }));
        Assert.Equal((QuotaState.Known, 100L, 150L), (quota.State, quota.Used, quota.Total));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    [InlineData("missing")]
    public void AProbeFailureIsUnknown(string failure)
    {
        Exception error = failure switch
        {
            "io" => new IOException("device"),
            "denied" => new UnauthorizedAccessException("no"),
            _ => new DriveNotFoundException("gone"),
        };
        Assert.Equal(QuotaState.Unknown, LocalVolumes.QuotaFor([@"C:\a"], new FakeVolumeSource([], error)).State);
    }

    [Theory]
    [InlineData(false, 100, 40)]
    [InlineData(true, 0, 0)]
    [InlineData(true, -1, 0)]
    public void ANotReadyOrSizelessVolumeIsUnknown(bool ready, long total, long free)
    {
        var quota = LocalVolumes.QuotaFor([@"C:\a", @"D:\b"], new FakeVolumeSource(new() { [@"C:\"] = Row(@"C:\", true, 100, 40), [@"D:\"] = Row(@"D:\", ready, total, free) }));
        Assert.Equal(QuotaState.Unknown, quota.State);
        Assert.Null(quota.Total);
    }

    [Fact]
    public void NoRootsIsUnknown() => Assert.Equal(QuotaState.Unknown, LocalVolumes.QuotaFor([], new FakeVolumeSource([])).State);

    private static VolumeRow Row(string name, bool ready, long total, long free)
        => new(name, "NTFS", ready, DriveType.Fixed, free, total);

    private sealed class FakeVolumeSource(Dictionary<string, VolumeRow> volumes, Exception? failure = null) : IVolumeSource
    {
        public IReadOnlyList<VolumeRow> Drives() => [.. volumes.Values];

        public VolumeRow? ForPath(string path)
        {
            if (failure is not null) { throw failure; }
            var volumeRoot = path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + @":\" : null;
            return volumeRoot is null ? null : volumes.FirstOrDefault(pair => string.Equals(pair.Key, volumeRoot, StringComparison.OrdinalIgnoreCase)).Value;
        }
    }
}
