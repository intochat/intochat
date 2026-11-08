using DigitalBrain.Capabilities.Connections.Broker;
using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Capabilities;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class InProcessAppRuntimeFacts : IAsyncLifetime
{
    private readonly string _sideloadRoot = Directory.CreateTempSubdirectory("inproc-sideload-").FullName;
    private IInProcessHost? _host;

    private IServiceProvider Services => (_host ?? throw new InvalidOperationException("Host not started.")).Services;

    public async ValueTask InitializeAsync()
    {
        var Folder = Directory.CreateDirectory(Path.Combine(_sideloadRoot, "notes"));
        await File.WriteAllTextAsync(Path.Combine(Folder.FullName, "app.cs"), Declarations.Of("notes", "1.0.0", "Notes", "Notes."));
        await File.WriteAllTextAsync(Path.Combine(Folder.FullName, "note.cs"), "static class Note { }\n");

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
                services.TryAddSingleton<ICapabilityConnections, EmptyCapabilityConnections>();
                services.TryAddSingleton<ICallFilter, CallFilter>();
                services.AddSingleton(sp =>
                {
                    var git = new GitSideload((_, _) => Task.FromResult(0), GitSideload.MaxBytes);
                    var catalog = new FeatureCatalog(directory: null, sandbox: null, dev: null, sideload: new SideloadDirectory(_sideloadRoot), git: git);
                    return new SideloadInstaller(catalog, git, (req, _) => Task.FromResult(InstallOutcome.Installed(req.Ref.App)));
                });
            });

        // Boot the silo WITHOUT IntoChat.OS.Modules.Microsoft.CSharp registered.
        _host = await factory.Start(
            new InProcessHostOptions
            {
                Modules =
                [
                    typeof(AppsModule),
                    typeof(FlutterModule),
                    typeof(FlameStubModule),
                ],
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        if (Directory.Exists(_sideloadRoot))
        {
            Directory.Delete(_sideloadRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompiledAppRunsOnTheInProcessHost()
    {
        const string brain = "inprocbrain";
        using var _ = StampCaller(brain);

        var appKey = IApp.Key(brain, "post");
        var appNeuron = SiloNeurons.Get<IApp>(Services, appKey);
        var bundle = PostApp.Bundle with
        {
            Files = new Dictionary<string, string>(StringComparer.Ordinal),
            CompiledType = typeof(PostApp).AssemblyQualifiedName,
        };
        await appNeuron.Import(bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "post", bundle.Version));

        var host = Services.GetRequiredService<InProcessAppHost>();
        await host.AssignType(appKey, typeof(PostApp), new Dictionary<string, string>(StringComparer.Ordinal), TestContext.Current.CancellationToken);
        await host.Request(appKey, "open", new PostOpen(), TestContext.Current.CancellationToken);

        var snapshot = await appNeuron.Read();
        Assert.NotNull(snapshot.Presentation);
        var mainView = await SiloNeurons.Get<IView>(Services, appKey + "/view").Read();
        Assert.Equal(System.Text.Json.JsonValueKind.Object, mainView?.View.GetProperty("root").ValueKind);

        // SideloadNeuron answers on the silo.
        var sideload = SiloNeurons.Get<ISideload>(Services, $"{brain}/sideload");
        var rows = await sideload.List();
        var entry = Assert.Single(rows);
        Assert.Equal("notes", entry.Name);
    }

    private static IDisposable StampCaller(string brain)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

    [DigitalBrain.Module("flame-stub")]
    private sealed class FlameStubModule : DigitalBrain.Module
    {
        public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
    }

    private sealed class EmptyCapabilityConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

