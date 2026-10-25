using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

[Trait("host", "own")]
public sealed class BrainJournalSeamFacts
{
    private static async Task<IntoChat.OS.Testing.ITestBrain> StartAsync(CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("journal-seam-").FullName;
        return await TestBrain.Create()
            .WithConfiguration(IntoChatOSNames.MasterKeyConfigurationKey, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .WithConfiguration(IntoChatOSNames.HomeKey, directory).StartAsync(ct);
    }

    [Fact]
    public async Task JournalAppendsThenReadsThroughThePublicSeam()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var journal = brain.Services.GetRequiredService<IBrainJournal>();
        var scope = BrainScope.Create("seam-account", "seam-brain").Id;
        Assert.Null(await journal.ReadAsync(scope, "seam-key", "seam-state", ct));
        var etag = await journal.AppendAsync(scope, "seam-key", "seam-state", null, BrainRecordChange.Write, [1, 2, 3], ct);
        var record = await journal.ReadAsync(scope, "seam-key", "seam-state", ct);
        Assert.NotNull(record);
        Assert.Equal(etag, record.ETag);
        Assert.Equal<byte>([1, 2, 3], record.Record);
    }

    [Fact]
    public async Task CheckSucceedsOnAFreshScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        Assert.True(await brain.Services.GetRequiredService<IBrainJournal>().CheckAsync(BrainScope.Create("fresh-account", "fresh-brain").Id, ct));
    }

    [Fact]
    public async Task ExportOmitsHardDeniedStates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var journal = brain.Services.GetRequiredService<IBrainJournal>();
        var scope = BrainScope.Create("export-seam-account", "export-seam-brain").Id;
        await journal.AppendAsync(scope, "vault", "vault", null, BrainRecordChange.Write, Encoding.UTF8.GetBytes("hidden-custody"), ct);
        using var output = new MemoryStream();
        await BrainJournalExport.WriteAsync(brain.Services, scope, (_, _, bytes) => Encoding.UTF8.GetString(bytes), output, ct);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("hidden-custody", text);
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("OmittedRecords").GetInt32() >= 1);
        Assert.False(BrainJournalExport.Allows("vault", typeof(string)));
    }
}
