using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Shipping;
using DigitalBrain.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Apps.Tests;

// Shipped apps are bundles the host verifies once and imports at startup: a green bundle
// lands as a live app in the publisher's brain, a red one stays out, and an unchanged
// version is a no-op.
public sealed class ShippedAppPublisherFacts
{
    [Fact]
    public async Task AGreenBundleImportsOnceARedOneStaysOutAndABumpedVersionReimports()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["passing"] = (0, "dbtest:pass Echo");
        runner.BySourceMarker["failing"] = (1, "dbtest:fail Echo\tWrong answer");
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(runner)).StartAsync(ct);
        var versions = new Dictionary<string, string> { ["first"] = "1.0.0", ["red"] = "1.0.0" };
        var sources = new IShippedAppSource[] { new Source("first", versions), new Source("red", versions, tests: "failing") };
        async Task Ship(string selection)
        {
            var content = new ShippedAppPublisher(
                brain.SiloServices.GetRequiredService<IGrainFactory>(),
                brain.SiloServices.GetRequiredService<ITestScriptRunner>(),
                sources, new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { [ShippedAppPublisher.ShipOnStartupKey] = selection }).Build(),
                NullLogger<ShippedAppPublisher>.Instance);
            await using var services = PublisherServices(content);
            var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
            await publisher.StartAsync(ct);
            await publisher.ExecuteTask!.WaitAsync(ct);
        }

        await Ship("false");
        var first = brain.Get<IApp>("first/apps/echo");
        Assert.Equal(AppStatus.Drafted, (await first.Read()).Status);

        await Ship("true");
        var imported = await first.Read();
        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Equal("1.0.0", imported.Version);
        // The imported copy is green: it can be exported onward.
        Assert.Equal("1.0.0", (await first.Export()).Version);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>("red/apps/echo").Read()).Status);

        // Same version again: no new history entry.
        var before = (await first.Read()).Versions.Length;
        await Ship("true");
        Assert.Equal(before, (await first.Read()).Versions.Length);

        // A bumped version re-imports.
        versions["first"] = "1.1.0";
        await Ship("true");
        Assert.Equal("1.1.0", (await first.Read()).Version);
    }

    [Fact]
    public async Task ADownstreamCancellationFailsThePackageWithoutStoppingThePublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(new CancelledRunner())).StartAsync(ct);
        var content = new ShippedAppPublisher(
            brain.SiloServices.GetRequiredService<IGrainFactory>(),
            brain.SiloServices.GetRequiredService<ITestScriptRunner>(),
            [new Source("first", new() { ["first"] = "1.0.0" })], new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [ShippedAppPublisher.ShipOnStartupKey] = "true" }).Build(),
            NullLogger<ShippedAppPublisher>.Instance);
        await using var services = PublisherServices(content);
        var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
        await publisher.StartAsync(ct);

        await publisher.ExecuteTask!.WaitAsync(ct);

        Assert.True(publisher.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>("first/apps/echo").Read()).Status);
    }

    [Fact]
    public async Task PlatformRestoresTheCallerAndUsesOnlyRegisteredPublisherIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        Caller.As("original");
        var original = CallerContextStamper.Require();
        try
        {
            var content = new RecordingContent();
            await using var services = PublisherServices(content);
            var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
            await publisher.StartAsync(ct);
            await publisher.ExecuteTask!.WaitAsync(ct);
            Assert.Equal(["first", "second"], content.Callers.Select(caller => caller.PrincipalId).ToArray());
            Assert.All(content.Callers, caller =>
            {
                Assert.Equal(CallerKind.Platform, caller.Kind);
                Assert.Equal(TrustedEdge.Platform, caller.StampedBy);
                Assert.Equal(caller.PrincipalId, caller.AccountId);
                Assert.Equal(caller.PrincipalId, caller.BrainId);
            });
            Assert.Equal(original, CallerContextStamper.Require());
        }
        finally { Caller.Clear(); }
    }

    private sealed class RecordingContent : IShippedContentPublisher
    {
        public string[] Publishers => ["first", "second"];
        public List<CallerContext> Callers { get; } = [];
        public Task PublishAsync(string publisher, CancellationToken cancellationToken)
        {
            Callers.Add(CallerContextStamper.Require());
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider PublisherServices(IShippedContentPublisher content)
        => new ServiceCollection()
            .AddSingleton(content)
            .AddSingleton<IHostApplicationLifetime>(new StartedLifetime())
            .AddShippedContentPublishing()
            .BuildServiceProvider();

    private sealed class CancelledRunner : ITestScriptRunner
    {
        public Task<AppTestRun> RunAsync(string appKey, string tests, CancellationToken cancellationToken)
            => throw new OperationCanceledException("The C# sandbox did not start.");
    }

    private sealed class Source(string publisher, Dictionary<string, string> versions, string tests = "passing") : IShippedAppSource
    {
        public string Publisher => publisher;
        public IReadOnlyList<AppBundle> Load() =>
        [new("echo", versions[publisher], new("Echo", "Echo input", [new("ask", "Echo")], [], []),
            "## Scenario: Echo\nEchoes.", new() { [AppFiles.TestsPath] = tests })];
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
