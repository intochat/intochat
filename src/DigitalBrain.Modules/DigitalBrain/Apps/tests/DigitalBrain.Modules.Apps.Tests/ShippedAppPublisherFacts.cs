using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using DigitalBrain.Platform.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalBrain.Modules.Apps.Tests;

// Shipped apps are bundles the host imports at startup into their publisher account's own brain: a
// bundle that imports and publishes lands as a live app there, and an unchanged version is a no-op.
public sealed class ShippedAppPublisherFacts
{
    [Fact]
    public async Task ABundleImportsOnceAndABumpedVersionReimports()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var first = await Register(brain, "first", ct);
        var versions = new Dictionary<string, string> { ["first"] = "1.0.0" };
        IShippedAppSource[] sources = [new Source("first", versions)];

        await Ship(brain, sources, "false", ct);
        var app = brain.Get<IApp>(IApp.Key(first.ScopeId, "echo"));
        Assert.Equal(AppStatus.Drafted, (await app.Read()).Status);

        await Ship(brain, sources, "true", ct);
        var imported = await app.Read();
        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.Equal("1.0.0", imported.Version);
        Assert.Equal("1.0.0", imported.PublishedVersion);
        Assert.Equal("1.0.0", (await app.Export()).Version);

        var before = (await app.Read()).Versions.Length;
        await Ship(brain, sources, "true", ct);
        Assert.Equal(before, (await app.Read()).Versions.Length);

        versions["first"] = "1.1.0";
        await Ship(brain, sources, "true", ct);
        Assert.Equal("1.1.0", (await app.Read()).Version);
    }

    [Fact]
    public async Task ABundleTheGateRefusesStaysOutAndTheOthersStillShip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(new BrokenEchoSandbox()))
            .StartAsync(ct);
        var first = await Register(brain, "first", ct);
        var red = await Register(brain, "red", ct);
        var versions = new Dictionary<string, string> { ["first"] = "1.0.0", ["red"] = "1.0.0" };

        var publisher = await Ship(brain, [new Source("red", versions, behavior: "// broken"), new Source("first", versions)], "true", ct);

        Assert.True(publisher.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(first.ScopeId, "echo")).Read()).Status);
        // Import landed the files but Publish refused; the next startup retries the same bundle.
        Assert.Null((await brain.Get<IApp>(IApp.Key(red.ScopeId, "echo")).Read()).PublishedVersion);
    }

    [Fact]
    public async Task AnUnparsableBundleFailsThatPackageWithoutStoppingThePublisher()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var first = await Register(brain, "first", ct);

        var publisher = await Ship(brain, [new Source("first", new() { ["first"] = "not-a-version" })], "true", ct);

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

    internal sealed class Source(string account, Dictionary<string, string> versions, string behavior = "// echo", string spec = "Echoes.") : IShippedAppSource
    {
        public string Account => account;
        public bool Writable => false;
        public void Write(AppBundle bundle) => throw new NotSupportedException();
        public IReadOnlyList<AppBundle> Load() =>
        [new("echo", versions[account], new("Echo", "Echo input", [new("ask", "Echo")], [], []),
            spec, new() { ["synapses/echo.cs"] = behavior })];
    }

    // Refuses exactly the broken behavior and accepts everything else.
    private sealed class BrokenEchoSandbox : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(files.Values.Any(source => source.Contains("broken", StringComparison.Ordinal))
                ? new ScriptCompilationCheck(false, [new("synapses/echo.cs", "CS1002", 1, "; expected")])
                : new ScriptCompilationCheck(true, []));
    }

    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
