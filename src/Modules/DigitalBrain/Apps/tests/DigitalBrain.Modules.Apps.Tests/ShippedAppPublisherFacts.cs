using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using DigitalBrain.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Apps.Tests;

// Shipped apps are bundles the host verifies once and imports at startup into their publisher
// account's own brain: a green bundle lands as a live app there, a red one stays out, and an
// unchanged version is a no-op.
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
        var first = await Register(brain, "first", ct);
        var red = await Register(brain, "red", ct);
        var versions = new Dictionary<string, string> { ["first"] = "1.0.0", ["red"] = "1.0.0" };
        IShippedAppSource[] sources = [new Source("first", versions), new Source("red", versions, tests: "failing")];

        await Ship(brain, sources, "false", ct);
        var app = brain.Get<IApp>(IApp.Key(first.ScopeId, "echo"));
        Assert.Equal(AppStatus.Drafted, (await app.Read()).Status);

        await Ship(brain, sources, "true", ct);
        var imported = await app.Read();
        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Equal("1.0.0", imported.Version);
        Assert.Equal("1.0.0", (await app.Export()).Version);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(red.ScopeId, "echo")).Read()).Status);

        var before = (await app.Read()).Versions.Length;
        await Ship(brain, sources, "true", ct);
        Assert.Equal(before, (await app.Read()).Versions.Length);

        versions["first"] = "1.1.0";
        await Ship(brain, sources, "true", ct);
        Assert.Equal("1.1.0", (await app.Read()).Version);
    }

    // Regression: the tests script runs as an App caller of the publisher's brain and drives its
    // scratch copy through the call filter. A pseudo-brain key ("intochat/apps/...") had no scope
    // and every such call was refused, so no shipped app could ever go green.
    [Fact]
    public async Task TheTestsScriptMayDriveItsScratchCopyInThePublishersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(new EdgeLikeRunner()))
            .StartAsync(ct);
        EdgeLikeRunner.Client = brain.Grains;
        var vendor = await Register(brain, "vendor", ct);

        await Ship(brain, [new Source("vendor", new() { ["vendor"] = "1.0.0" })], "true", ct);

        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(vendor.ScopeId, "echo")).Read()).Status);
    }

    [Fact]
    public async Task ADownstreamCancellationFailsThePackageWithoutStoppingThePublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ITestScriptRunner>(new CancelledRunner())).StartAsync(ct);
        var first = await Register(brain, "first", ct);

        var publisher = await Ship(brain, [new Source("first", new() { ["first"] = "1.0.0" })], "true", ct);

        Assert.True(publisher.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(first.ScopeId, "echo")).Read()).Status);
    }

    [Fact]
    public async Task PlatformStampsEachPublisherAccountsOwnBrainAndRestoresTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        Caller.As("original");
        var original = CallerContextStamper.Require();
        try
        {
            var content = new RecordingContent();
            var directory = new FixedPublishers(new PublisherBrain("first", "account-1", "brain-1"), new PublisherBrain("second", "account-2", "brain-2"));
            await using var services = PublisherServices(content, directory);
            var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
            await publisher.StartAsync(ct);
            await publisher.ExecuteTask!.WaitAsync(ct);
            Assert.Equal(["first", "second"], content.Callers.Select(caller => caller.PrincipalId).ToArray());
            Assert.Equal(["account-1", "account-2"], content.Callers.Select(caller => caller.AccountId).ToArray());
            Assert.Equal(["brain-1", "brain-2"], content.Callers.Select(caller => caller.BrainId).ToArray());
            Assert.All(content.Callers, caller =>
            {
                Assert.Equal(CallerKind.Platform, caller.Kind);
                Assert.Equal(TrustedEdge.Platform, caller.StampedBy);
            });
            Assert.Equal(original, CallerContextStamper.Require());
        }
        finally { Caller.Clear(); }
    }

    [Fact]
    public async Task AnUnknownPublisherAccountIsSkippedAndTheOthersStillShip()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new RecordingContent(["missing", "second"]);
        await using var services = PublisherServices(content, new FixedPublishers(new PublisherBrain("second", "account-2", "brain-2")));
        var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
        await publisher.StartAsync(ct);
        await publisher.ExecuteTask!.WaitAsync(ct);
        Assert.Equal(["second"], content.Callers.Select(caller => caller.PrincipalId).ToArray());
    }

    internal static async Task<PublisherBrain> Register(ModuleBrain brain, string username, CancellationToken ct)
    {
        await brain.Get<IIdentityDirectory>(IdentityGrains.Directory).RegisterAsync(username, "correct-password", username, ct);
        return await brain.SiloServices.GetRequiredService<IPublisherDirectory>().Resolve(username, ct);
    }

    internal static async Task<BackgroundService> Ship(ModuleBrain brain, IShippedAppSource[] sources, string selection, CancellationToken ct)
    {
        var content = new ShippedAppPublisher(
            brain.SiloServices.GetRequiredService<IGrainFactory>(),
            brain.SiloServices.GetRequiredService<ITestScriptRunner>(),
            sources, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [ShippedAppPublisher.ShipOnStartupKey] = selection }).Build(),
            NullLogger<ShippedAppPublisher>.Instance);
        await using var services = PublisherServices(content, brain.SiloServices.GetRequiredService<IPublisherDirectory>());
        var publisher = Assert.IsAssignableFrom<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()));
        await publisher.StartAsync(ct);
        await publisher.ExecuteTask!.WaitAsync(ct);
        return publisher;
    }

    private sealed class RecordingContent(string[]? accounts = null) : IShippedContentPublisher
    {
        public string[] Accounts => accounts ?? ["first", "second"];
        public List<CallerContext> Callers { get; } = [];
        public Task PublishAsync(PublisherBrain publisher, CancellationToken cancellationToken)
        {
            Callers.Add(CallerContextStamper.Require());
            return Task.CompletedTask;
        }
    }

    private sealed class FixedPublishers(params PublisherBrain[] known) : IPublisherDirectory
    {
        public Task<PublisherBrain> Resolve(string username, CancellationToken ct)
            => known.FirstOrDefault(publisher => publisher.Username == username) is { } found
                ? Task.FromResult(found)
                : throw new InvalidOperationException($"Publisher account '{username}' does not exist.");
    }

    internal static ServiceProvider PublisherServices(IShippedContentPublisher content, IPublisherDirectory directory)
        => new ServiceCollection()
            .AddLogging()
            .AddSingleton(content)
            .AddSingleton(directory)
            .AddSingleton<IHostApplicationLifetime>(new StartedLifetime())
            .AddShippedContentPublishing()
            .BuildServiceProvider();

    // Plays the sandbox edge: the script speaks as an App caller of the brain the runner was
    // stamped with, through the client, so every call crosses the neuron call filter.
    private sealed class EdgeLikeRunner : ITestScriptRunner
    {
        public static IGrainFactory? Client { get; set; }

        public async Task<AppTestRun> RunAsync(string appKey, string tests, CancellationToken cancellationToken)
        {
            var owner = CallerContextStamper.Require();
            CallerContextStamper.Stamp(owner with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy });
            try
            {
                await Client!.GetGrain<IApp>(appKey).Configure(new(), new());
                return new([new("Echo", true, "")], 0);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new([new("Echo", false, error.Message)], 1); }
            finally { CallerContextStamper.Stamp(owner); }
        }
    }

    private sealed class CancelledRunner : ITestScriptRunner
    {
        public Task<AppTestRun> RunAsync(string appKey, string tests, CancellationToken cancellationToken)
            => throw new OperationCanceledException("The C# sandbox did not start.");
    }

    internal sealed class Source(string account, Dictionary<string, string> versions, string tests = "passing") : IShippedAppSource
    {
        public string Account => account;
        public bool Writable => false;
        public void Write(AppBundle bundle) => throw new NotSupportedException();
        public IReadOnlyList<AppBundle> Load() =>
        [new("echo", versions[account], new("Echo", "Echo input", [new("ask", "Echo")], [], []),
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
