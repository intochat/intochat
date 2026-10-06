using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Apps.Tests;

// The repository folder and the publisher's brain agree at the published version: whichever is
// newer wins at startup, and publishing in the brain writes the folder.
public sealed class ShippedAppSyncFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("shipped-sync-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ANewerRepositoryVersionIsImportedAndPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var vendor = await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(Bundle("1.0.0", "It echoes."));

        await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);

        var app = await brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo")).Read();
        Assert.Equal("1.0.0", app.PublishedVersion);
    }

    [Fact]
    public async Task ANewerBrainVersionIsWrittenBackToAWritableFolderAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var vendor = await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(Bundle("1.0.0", "It echoes."));
        await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);
        var app = brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo"));
        await PublishEdit(app, vendor, "It echoes twice.");
        var published = (await app.Read()).PublishedVersion!;
        // The folder went stale behind the brain's back (a checkout of an older commit).
        source.Write(Bundle("1.0.0", "It echoes."));

        await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);

        var folder = Assert.Single(source.Load());
        Assert.Equal(published, folder.Version);
        Assert.Contains("It echoes twice.", folder.Spec, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingInTheBrainWritesTheFolder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var vendor = await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(Bundle("1.0.0", "It echoes."));
        await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);
        var writeBack = new ShippedAppWriteBack(
            brain.SiloServices.GetRequiredService<LocalSignalHub>(), brain.SiloServices.GetRequiredService<IGrainFactory>(),
            brain.SiloServices.GetRequiredService<IPublisherDirectory>(), [source], NullLogger<ShippedAppWriteBack>.Instance);
        await writeBack.StartAsync(ct);
        try
        {
            var app = brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo"));
            await PublishEdit(app, vendor, "It echoes three times.");
            var published = (await app.Read()).PublishedVersion!;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (Assert.Single(source.Load()).Version != published)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "The folder never received the published version.");
                await Task.Delay(100, ct);
            }
            Assert.Contains("It echoes three times.", Assert.Single(source.Load()).Spec, StringComparison.Ordinal);
        }
        finally { await writeBack.StopAsync(ct); }
    }

    // Startup publishes the repository's own version; writing it back would only reformat the
    // folder and leave every developer a diff after each start.
    [Fact]
    public async Task PublishingTheVersionTheFolderAlreadyHoldsLeavesTheFolderUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(Bundle("1.0.0", "It echoes."));
        var manifest = Path.Combine(_root, "Echo", "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest) + "\n");
        var before = File.ReadAllText(manifest);
        var writeBack = new ShippedAppWriteBack(
            brain.SiloServices.GetRequiredService<LocalSignalHub>(), brain.SiloServices.GetRequiredService<IGrainFactory>(),
            brain.SiloServices.GetRequiredService<IPublisherDirectory>(), [source], NullLogger<ShippedAppWriteBack>.Instance);
        await writeBack.StartAsync(ct);
        try
        {
            await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);
            await Task.Delay(500, ct);
            Assert.Equal(before, File.ReadAllText(manifest));
        }
        finally { await writeBack.StopAsync(ct); }
    }

    // Write-back only fast-forwards: a folder holding work the brain never saw (an edit without a
    // version bump) is left alone, at startup and on publish, instead of silently overwritten.
    [Fact]
    public async Task AFolderWithUnpublishedEditsIsNeverOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var vendor = await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(Bundle("1.0.0", "It echoes."));
        await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);
        var writeBack = new ShippedAppWriteBack(
            brain.SiloServices.GetRequiredService<LocalSignalHub>(), brain.SiloServices.GetRequiredService<IGrainFactory>(),
            brain.SiloServices.GetRequiredService<IPublisherDirectory>(), [source], NullLogger<ShippedAppWriteBack>.Instance);
        await writeBack.StartAsync(ct);
        var spec = Path.Combine(_root, "Echo", "app.spec.md");
        File.WriteAllText(spec, "## Scenario: Echo\nMy uncommitted work.\n");
        try
        {
            await PublishEdit(brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo")), vendor, "It echoes twice.");
            await Task.Delay(500, ct);
            Assert.Contains("My uncommitted work.", File.ReadAllText(spec), StringComparison.Ordinal);

            await ShippedAppPublisherFacts.Ship(brain, [source], "true", ct);
            Assert.Contains("My uncommitted work.", File.ReadAllText(spec), StringComparison.Ordinal);
        }
        finally { await writeBack.StopAsync(ct); }
    }

    [Fact]
    public async Task AReadOnlyFolderIsNeverWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var vendor = await ShippedAppPublisherFacts.Register(brain, "vendor", ct);
        new DirectoryShippedAppSource(_root, "vendor", writable: true).Write(Bundle("1.0.0", "It echoes."));
        var readOnly = new DirectoryShippedAppSource(_root, "vendor");
        await ShippedAppPublisherFacts.Ship(brain, [readOnly], "true", ct);
        await PublishEdit(brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo")), vendor, "It echoes twice.");

        await ShippedAppPublisherFacts.Ship(brain, [readOnly], "true", ct);

        Assert.Equal("1.0.0", Assert.Single(readOnly.Load()).Version);
    }

    private static Task<ModuleBrain> StartAsync(CancellationToken ct)
    {
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["passing"] = (0, "dbtest:pass Echo");
        return ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(runner)).StartAsync(ct);
    }

    private static AppBundle Bundle(string version, string behavior) =>
        new("echo", version, new("Echo", "Echo input", [new("ask", "Echo")], [], []),
            $"## Scenario: Echo\n{behavior}\n", new() { [AppFiles.TestsPath] = "passing" });

    // An edit in the brain, verified green and published, as the publisher would do in the shell.
    private static async Task PublishEdit(IApp app, PublisherBrain publisher, string behavior)
    {
        await app.SaveSpec($"## Scenario: Echo\n{behavior}\n", Guid.NewGuid());
        var version = (await app.Read()).Version;
        await app.RecordVerdicts([new("Echo", true, "")], green: true, version);
        // Publishing acts as the publisher, the way the shell's signed-in session does.
        DigitalBrain.Framework.Enforcement.CallerContextStamper.Stamp(new DigitalBrain.Framework.Enforcement.CallerContext
        {
            PrincipalId = publisher.Username,
            AccountId = publisher.AccountId,
            BrainId = publisher.BrainId,
            Kind = DigitalBrain.Framework.Enforcement.CallerKind.Platform,
            StampedBy = DigitalBrain.Framework.Enforcement.TrustedEdge.Platform,
        });
        try { await app.Publish(); }
        finally { Caller.Clear(); }
    }
}
