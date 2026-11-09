using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// Host code that publishes a view is the platform of its brain and stamps itself so: each host path runs with no ambient caller and
// its view lands, while a bare publish nobody stamped is still refused.
public sealed class HostStampFacts : IAsyncLifetime
{
    private IInProcessHost? _host;

    private IServiceProvider Services => (_host ?? throw new InvalidOperationException("Host not started.")).Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var signalAssemblies = new[]
        {
            typeof(AppsModule).Assembly,
            typeof(IApp).Assembly,
            typeof(FlutterModule).Assembly,
            typeof(IView).Assembly,
            typeof(DigitalBrain.Signals.Activated).Assembly,
        }.Distinct().ToArray();
        var factory = new LocalSiloHostFactory(
            new BrainStorageRegistration(signalAssemblies),
            services =>
            {
                AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
                services.TryAddSingleton<ICapabilityConnections, NoConnections>();
                services.TryAddSingleton<ICallFilter, CallFilter>();
                services.AddIntoChatOSClient();
            });
        _host = await factory.Start(new InProcessHostOptions { Modules = [typeof(AppsModule), typeof(FlutterModule), typeof(FlameStub)] }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) { await _host.DisposeAsync(); }
    }

    private static void NoCaller() => Orleans.Runtime.RequestContext.Remove(RuntimeCallerContext.RequestContextKey);

    private async Task<bool> Published(string key) => await SiloNeurons.Get<IView>(Services, key).Read() is not null;

    [Fact]
    public async Task ABarePublishNobodyStampedIsRefused()
    {
        NoCaller();
        Assert.False(RuntimeCallerContext.TryGet(out _));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SiloNeurons.Get<IView>(Services, "barebrain/apps/notes/view").Publish(1, new DigitalBrain.UI.View("Notes", new DigitalBrain.UI.Column([]))));
    }

    [Fact]
    public async Task ThePresentationSinkPublishesAnAppsViewsWithNoAmbientCaller()
    {
        const string brain = "sinkbrain";
        NoCaller();
        var sink = Services.GetRequiredService<AppPresentationSink>();

        await sink.Present(IApp.Key(brain, "notes"), new DigitalBrain.UI.View("Side", new DigitalBrain.UI.Column([])), new DigitalBrain.UI.View("Notes", new DigitalBrain.UI.Column([])), Ct);

        Assert.False(RuntimeCallerContext.TryGet(out _));
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Platform(brain, brain, brain)))
        {
            Assert.True(await Published($"{brain}/apps/notes/side"));
            Assert.True(await Published($"{brain}/apps/notes/view"));
        }
    }

    [Fact]
    public async Task TheCompiledAppsHostPublishesTheDesktopViewWithNoAmbientCaller()
    {
        const string brain = "desktopbrain";
        NoCaller();
        var compiled = ActivatorUtilities.CreateInstance<CompiledAppsHost>(Services);

        await compiled.EnsureDesktopViewAsync(brain, Ct);

        Assert.True(await Published($"{brain}/apps/desktop/view"));
    }

    [Fact]
    public async Task TheFilesWorkspacePublishesItsViewWithNoAmbientCaller()
    {
        const string brain = "filesbrain";
        NoCaller();
        var compiled = ActivatorUtilities.CreateInstance<CompiledAppsHost>(Services);

        await compiled.EnsureOpenAppViewAsync(brain, IntoChat.Files.FilesApp.AppName, Ct);

        Assert.True(await Published($"{brain}/apps/files/view"));
    }

    [DigitalBrain.Module("flame-stub-host-stamp")]
    private sealed class FlameStub : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    private sealed class NoConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default) where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default) => Task.CompletedTask;
    }
}
