using DigitalBrain.Capabilities.Connections.Broker;
using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Apps;
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
            typeof(Activated).Assembly,
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
        Assert.NotNull(mainView?.Root);

        // SideloadNeuron answers on the silo.
        var sideload = SiloNeurons.Get<ISideload>(Services, $"{brain}/sideload");
        var rows = await sideload.List();
        var entry = Assert.Single(rows);
        Assert.Equal("notes", entry.Name);
    }

    [Fact]
    public async Task PrebuiltBundleRunsWithoutTheCSharpModule()
    {
        const string brain = "prebuiltbrain";
        using var _ = StampCaller(brain);

        // Ensure CSharp module's IScriptSandbox is NOT registered in this silo.
        Assert.Null(Services.GetService<IScriptSandbox>());

        var assemblyBytes = CompilePrebuiltCalculatorAssembly();
        var hash = Convert.ToHexStringLower(SHA256.HashData(assemblyBytes));
        var prebuilt = new Dictionary<string, PrebuiltBehavior>(StringComparer.Ordinal)
        {
            ["calc.dll"] = new(assemblyBytes, [], hash),
        };

        var manifest = new AppManifest(
            Title: "Calculator",
            Description: "Prebuilt calculator.",
            Operations: [new("open", "Open calculator."), new("press", "Press key.")],
            Settings: [],
            Slots: []);
        var bundle = new AppBundle(
            Name: "calculator",
            Version: "1.0.0",
            Manifest: manifest,
            Spec: "# Calculator",
            Files: new Dictionary<string, string>(StringComparer.Ordinal),
            PrebuiltBehaviors: prebuilt);

        var appKey = IApp.Key(brain, "calculator");
        var appNeuron = SiloNeurons.Get<IApp>(Services, appKey);
        await appNeuron.Import(bundle, new AppOrigin("test", new AppSource.Shipped(), "calculator", "1.0.0"));

        var host = Services.GetRequiredService<InProcessAppHost>();
        await host.Request(appKey, "press", "7", TestContext.Current.CancellationToken);
        await host.Request(appKey, "press", "+", TestContext.Current.CancellationToken);
        await host.Request(appKey, "press", "5", TestContext.Current.CancellationToken);
        await host.Request(appKey, "press", "=", TestContext.Current.CancellationToken);

        var snapshot = await appNeuron.Read();
        Assert.NotNull(snapshot.Presentation);
        var mainView = await SiloNeurons.Get<IView>(Services, appKey + "/view").Read();
        Assert.NotNull(mainView?.Root);
    }

    [Fact]
    public void SourceBundleStillGoesToTheCSharpRuntime()
    {
        var inproc = new InProcessAppRuntime(Services);
        var csharp = new CSharpAppRuntime(Services);

        var sourceBundle = new AppBundle(
            Name: "notes",
            Version: "1.0.0",
            Manifest: new AppManifest("Notes", "Notes.", [], [], []),
            Spec: "",
            Files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["note.cs"] = "static class Note { }",
            });

        Assert.False(inproc.Accepts(sourceBundle));
        Assert.True(csharp.Accepts(sourceBundle));
    }

    private static IDisposable StampCaller(string brain)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

    private static byte[] CompilePrebuiltCalculatorAssembly()
    {
        const string source = """
            using System.Globalization;
            using DigitalBrain;
            using DigitalBrain.UI;

            public sealed record CalcState(string Display = "0", double? Accumulator = null, string? Op = null, bool Reset = false)
            {
                public CalcState() : this("0") { }
            }

            [App("calculator")]
            public sealed class PrebuiltCalculatorApp : App<CalcState>
            {
                [On]
                public void Open() => Set(State);

                [On]
                public void Press(string key)
                {
                    if (key is "+" or "-" or "*" or "/")
                    {
                        var v = double.Parse(State.Display, CultureInfo.InvariantCulture);
                        Set(new CalcState(State.Display, v, key, true));
                        return;
                    }
                    if (key == "=" && State.Op == "+" && State.Accumulator is { } acc)
                    {
                        var r = acc + double.Parse(State.Display, CultureInfo.InvariantCulture);
                        Set(new CalcState(r.ToString("G15", CultureInfo.InvariantCulture)));
                        return;
                    }
                    var next = State.Reset || State.Display == "0" ? key : State.Display + key;
                    Set(State with { Display = next, Reset = false });
                }

                public View Render() => new("Calculator", new Column([new Text(State.Display)]));
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            $"PrebuiltCalc_{Guid.NewGuid():N}",
            [syntaxTree],
            trustedAssemblies,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return ms.ToArray();
    }

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
