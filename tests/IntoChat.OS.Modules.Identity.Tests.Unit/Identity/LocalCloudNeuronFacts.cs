using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Authority;

public sealed class LocalCloudNeuronFacts
{
    [Fact]
    public async Task LocalCloudNeuronReportsKnownQuotaAndEnforcesPerBrainMaxStorageBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = new LocalCloudStorage { MaxStorageBytes = 100 };

        var initial = await cloud.Describe(ct);
        Assert.Equal(QuotaState.Known, initial.Quota.State);
        Assert.Equal(0L, initial.Quota.Used);
        Assert.Equal(100L, initial.Quota.Total);

        // Write 60 bytes — succeeds
        await cloud.Write(new WritePlan(
            WriteKind.WriteFile,
            Source: null,
            TargetFolder: new ItemId(LocalCloudNeuron.RootIdValue),
            Name: "first.txt",
            Conflict: ConflictPolicy.Overwrite,
            Inline: new byte[60]), ct);

        var afterFirst = await cloud.Describe(ct);
        Assert.Equal(60L, afterFirst.Quota.Used);
        Assert.Equal(100L, afterFirst.Quota.Total);

        // Write 50 more bytes to a second file (total 110 > 100) — throws QuotaExceeded
        await Assert.ThrowsAsync<QuotaExceeded>(() => cloud.Write(new WritePlan(
            WriteKind.WriteFile,
            Source: null,
            TargetFolder: new ItemId(LocalCloudNeuron.RootIdValue),
            Name: "second.txt",
            Conflict: ConflictPolicy.Overwrite,
            Inline: new byte[50]), ct));

        // Overwrite first.txt with 80 bytes (total 80 <= 100) — succeeds
        await cloud.Write(new WritePlan(
            WriteKind.WriteFile,
            Source: null,
            TargetFolder: new ItemId(LocalCloudNeuron.RootIdValue),
            Name: "first.txt",
            Conflict: ConflictPolicy.Overwrite,
            Inline: new byte[80]), ct);

        var afterOverwrite = await cloud.Describe(ct);
        Assert.Equal(80L, afterOverwrite.Quota.Used);
    }
}
