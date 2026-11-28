using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Host.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InstalledAppsFacts
{
    private sealed class FakeBrainObserver(params BrainInstance[] instances) : IBrainObserver
    {
        public async IAsyncEnumerable<BrainInstance> Instances<TInterface>([EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TInterface : class
        {
            await Task.Yield();
            foreach (var instance in instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return instance;
            }
        }

        public IAsyncEnumerable<BrainInstance> Instances(string? typeName, CancellationToken cancellationToken = default)
            => Instances<object>(cancellationToken);

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult<BrainType[]>([]);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => Task.FromResult<ObservedWire[]>([]);
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<SignalDelivery>();
        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
    }

    private sealed class FakeNeuronProxies(IReadOnlyDictionary<string, IApp> apps) : INeuronProxies
    {
        public TInterface Get<TInterface>(string key) where TInterface : class
            => typeof(TInterface) == typeof(IApp) && (apps.TryGetValue(key, out var app) || apps.TryGetValue("local/" + key, out app))
                ? (TInterface)app
                : throw new KeyNotFoundException($"No fake neuron for {typeof(TInterface).Name} '{key}'.");

        public TInterface Get<TInterface>(NeuronId id) where TInterface : class
            => Get<TInterface>(id.Key);
    }

    private class FakeAppProxy : DispatchProxy
    {
        public AppSnapshot Snapshot { get; set; } = null!;
        public int ReadCount { get; private set; }

        public static (IApp Proxy, FakeAppProxy State) Create(AppSnapshot snapshot)
        {
            var proxy = DispatchProxy.Create<IApp, FakeAppProxy>();
            var state = (FakeAppProxy)(object)proxy;
            state.Snapshot = snapshot;
            return (proxy, state);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IApp.Read))
            {
                ReadCount++;
                return Task.FromResult(Snapshot);
            }
            throw new NotSupportedException($"Unexpected call to {targetMethod?.Name}");
        }
    }

    [Neuron("ino.synapses")]
    private sealed class FakeSynapseStudio : DigitalBrain.Neurons.Neuron, ISynapseStudio
    {
        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => Task.FromResult(new UnitDeployResult(true, "ok", DeployStatus.Deployed, "h", new Dictionary<string, InstallOutcome>()));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("ok"));
        public Task<DeployedSource[]> List() => Task.FromResult(new[]
        {
            new DeployedSource("weather-watch", "hash1", ["weather-watch"], DeployStatus.Deployed, null, DateTimeOffset.UtcNow),
            new DeployedSource("ino", "hash2", ["ino"], DeployStatus.Deployed, null, DateTimeOffset.UtcNow),
        });
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("none"));
    }

    private static AppSnapshot SnapshotFor(string name, AppManifest manifest)
        => new(name, manifest, "", new(), new(), new(), "1.0.0", default, [], []);

    [Fact]
    public async Task ListMergesCompiledRuntimeAndUnitsExcludesServicesAndHeadlessAndSkipsCompiledGrainReads()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        RuntimeHelpers.RunModuleConstructor(typeof(PhotoCraftApp).Assembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(AppsModule).Assembly.ManifestModule.ModuleHandle);

        var brainId = new BrainId("local");
        var (notesAppProxy, notesApp) = FakeAppProxy.Create(SnapshotFor("notes", new AppManifest(
            Title: "Quick Notes",
            Description: "Personal scratchpad",
            Operations: [],
            Settings: [],
            Slots: [],
            Icon: "file-text",
            Category: "Workspace",
            Launch: AppLaunch.Windowed)));
        var (headlessAppProxy, _) = FakeAppProxy.Create(SnapshotFor("bg-sync", new AppManifest(
            Title: "Background Sync",
            Description: "Headless worker",
            Operations: [],
            Settings: [],
            Slots: [],
            Headless: true)));
        var (compiledInoAppProxy, compiledInoApp) = FakeAppProxy.Create(SnapshotFor("ino", new AppManifest(
            Title: "Should Not Be Read",
            Description: "",
            Operations: [],
            Settings: [],
            Slots: [])));

        var notesKey = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "notes").Key;
        var headlessKey = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "bg-sync").Key;
        var inoKey = IntoChat.OS.Host.Primitives.AppRef.Of(brainId, "ino").Key;

        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(PhotoCraftApp).Assembly]);
        var proxies = new FakeNeuronProxies(new Dictionary<string, IApp>(StringComparer.Ordinal)
        {
            [notesKey] = notesAppProxy,
            [headlessKey] = headlessAppProxy,
            [inoKey] = compiledInoAppProxy,
        });
        var now = DateTimeOffset.UtcNow;
        var observer = new FakeBrainObserver(
            new BrainInstance("app", notesKey, now, null),
            new BrainInstance("app", headlessKey, now, null),
            new BrainInstance("app", inoKey, now, null));

        var sp = new ServiceCollection()
            .AddSingleton(catalog)
            .AddSingleton<INeuronProxies>(proxies)
            .AddSingleton<IBrainObserver>(observer)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = sp);
        os.Register<InstalledAppsNeuron>();
        os.Register<FakeSynapseStudio>();

        var installed = os.Get<IInstalledApps>(new NeuronId(brainId, "apps.installed", IInstalledApps.DefaultKey));
        var tiles = await installed.List();

        // Excludes ShippedApps.Services ("desktop") and Headless ("bg-sync")
        Assert.DoesNotContain(tiles, t => t.Id == "desktop");
        Assert.DoesNotContain(tiles, t => t.Id == "bg-sync");

        // Compiled apps include standalone bundle ("files"), [Screen] + Bundle ("photocraft", "ino"), and bundle-less [Screen] ("customer-researcher")
        var files = Assert.Single(tiles, t => t.Id == "files");
        Assert.Equal("Files", files.Title);
        Assert.Equal("Workspace", files.Category);
        Assert.Equal(2, files.Dock);
        Assert.Equal("compiled", files.Kind);

        var photo = Assert.Single(tiles, t => t.Id == "photocraft");
        Assert.Equal("PhotoCraft", photo.Title);
        Assert.Equal("artcraft:photocraft", photo.Icon);
        Assert.Equal("Studio", photo.Category);
        Assert.Null(photo.Dock);
        Assert.Equal(AppLaunch.Maximized, photo.Launch);
        Assert.Equal("compiled", photo.Kind);

        var researcher = Assert.Single(tiles, t => t.Id == "customer-researcher");
        Assert.Equal("Customer Researcher", researcher.Title);
        Assert.Equal("Workspace", researcher.Category);
        Assert.Equal("compiled", researcher.Kind);

        // Runtime app appears once with Kind == "runtime"
        var notes = Assert.Single(tiles, t => t.Id == "notes");
        Assert.Equal("Quick Notes", notes.Title);
        Assert.Equal("file-text", notes.Icon);
        Assert.Equal("Workspace", notes.Category);
        Assert.Equal("runtime", notes.Kind);
        Assert.Equal(1, notesApp.ReadCount);

        // Compiled IDs are skipped during runtime IApp grain reads (0 grain hops for "ino")
        Assert.Equal(0, compiledInoApp.ReadCount);
        var ino = Assert.Single(tiles, t => t.Id == "ino");
        Assert.Equal("compiled", ino.Kind);
        Assert.Equal(1, ino.Dock);

        // Deployed Synapse unit appears once with Kind == "unit", and colliding unit "ino" is deduplicated
        var weather = Assert.Single(tiles, t => t.Id == "weather-watch");
        Assert.Equal("Weather Watch", weather.Title);
        Assert.Equal("zap", weather.Icon);
        Assert.Equal("Micro-App", weather.Category);
        Assert.Equal("unit", weather.Kind);

        // Docked apps come first in Dock order
        var docked = tiles.Where(t => t.Dock is not null).ToArray();
        Assert.True(docked.Length >= 4);
        for (var i = 1; i < docked.Length; i++)
        {
            Assert.True(docked[i - 1].Dock <= docked[i].Dock);
        }
    }
}
