using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Circles.Tests;

// A host with the Apps and Circles modules over the repository's features folder and a scripted connector, and circle
// brains born the way a circle is born (T26-1): the trust side records a brain of kind Circle, and its creator gives
// it life. People act inside a circle as the /brains/{circle} route stamps them.
internal static class CircleBrainRooms
{
    public const string Connector = "shared";

    public static readonly string Features = Path.Combine(FindRoot(), "features");

    public static Task<ITestBrain> Start(CancellationToken ct, Func<TestBrain, TestBrain>? configure = null)
    {
        var builder = Compose(TestBrain.Create());
        return (configure?.Invoke(builder) ?? builder).StartAsync(ct);
    }

    public static TestBrain Compose(TestBrain brain)
        => brain.WithModule<AppsModule, AppsOptions>(options => options.Features = Features)
            .WithModule<CirclesModule>().WithModule<SharedConnectorModule>()
            // No csharp module here: the programs are data, deployed nowhere.
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime, NoRuntime>();
            });

    public static async Task<string> Create(ITestBrain brain, Person creator, string name = "friends")
    {
        var provision = await brain.Services.GetRequiredService<IIdentityClient>()
            .CreateBrain(creator.AccountId, creator.PrincipalId, Guid.NewGuid().ToString("N"), name, BrainKind.Circle);
        var circle = provision.Member.BrainId;
        using (creator.AsCallerIn(circle)) { await brain.Get<IBrain>(IBrain.Key(circle)).Create(new Owner(creator.AccountId)); }
        return circle;
    }

    public static async Task<T> In<T>(this Person person, string brainId, Func<Task<T>> call)
    {
        using (person.AsCallerIn(brainId)) { return await call(); }
    }

    public static async Task In(this Person person, string brainId, Func<Task> call)
    {
        using (person.AsCallerIn(brainId)) { await call(); }
    }

    public static IGrainFactory Grains(ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();
    public static IntoChat.OS.Runtime.IPlainNeurons Plain(ITestBrain brain) => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();

    public static ICircle Circle(ITestBrain brain, string circle) => brain.Get<ICircle>(ICircle.Key(circle));

    public static async Task Join(ITestBrain brain, string circle, Person creator, Person member)
    {
        await creator.In(circle, () => Circle(brain, circle).Invite(member.AccountId));
        await member.In(circle, () => Circle(brain, circle).Accept());
    }

    // An app of the person's own brain, its "db" slot bound to the person's connected `connectionId` when given.
    public static async Task Own(ITestBrain brain, Person person, string name, string? connectionId = null, Dictionary<string, string>? files = null)
    {
        using (person.AsCaller())
        {
            var app = brain.Get<IApp>(IApp.Key(person.BrainId, name));
            await app.Import(new AppBundle(name, "1.0.0", Manifest(name), $"# {name}", files ?? []));
            if (connectionId is null) { return; }
            await Plain(brain).GetConnection(ConnectionKeys.For(person.BrainId, Connector, connectionId)).Connect("api-key", "secret-" + connectionId);
            await app.Configure([], new() { ["db"] = connectionId });
        }
    }

    public static AppManifest Manifest(string name) => new(name, "An app shared into a circle.", [new("go", "Go.")], [], [new("db", Connector)]);

    // A synapse in shape whose body the marker sandbox refuses to compile.
    public const string NotCompiling = """
        await using var brain = await IntoChat.OS.Client.IntoChatOSClient.ConnectAsync(args);
        await foreach (var request in brain.On(brain.Get<IntoChat.OS.Modules.Apps.IApp>(brain.Setting("App")!), "go", brain.Stopping)) { DoesNotCompile(); }
        """;

    // Compiles everything except a file that calls DoesNotCompile.
    public sealed class MarkerSandbox : IScriptSandbox
    {
        public bool CanRun => false;
        public string AuthoringDescription => "marker";

        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken)
            => ReadContracts([moduleId], cancellationToken);

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));

        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
        {
            var failing = files.Where(file => file.Value.Contains("DoesNotCompile()", StringComparison.Ordinal))
                .Select(file => new ScriptCompilationDiagnostic(file.Key, "CS0103", 2, "The name 'DoesNotCompile' does not exist in the current context")).ToArray();
            return Task.FromResult(new ScriptCompilationCheck(failing.Length == 0, failing));
        }

        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
    }

    private sealed class NoRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException($"Intochat.OS.slnx was not found above {AppContext.BaseDirectory}.");
    }
}

// A connector whose use operation echoes which brain's connection answered.
public sealed class SharedConnectorModule() : Module(new ModuleId(CircleBrainRooms.Connector))
{
    public static ConnectorDefinition Connector => new(CircleBrainRooms.Connector, "Shared", ["api-key", "oauth"], ["shared-oauth"])
    { Neurons = [typeof(IShared)], OAuthMethods = [new("oauth", "https://shared.test/authorize", "https://shared.test/token", [], "shared-oauth")] };
    public static IntegrationSchema Integration => IntegrationSchema.For("shared-oauth", "Shared OAuth");
    public override Type? Neurons => typeof(SharedConnectorNeurons);
    public override void Configure(ISiloBuilder silo) { }
}

[DigitalBrain.Contracts.NeuronAlias("shared"), DigitalBrain.Contracts.DefaultNeuronType("shared")]
public interface IShared
{
    Task<string> Use();
}

[GenerateSerializer, Alias("shared.state")]
public sealed class SharedState : ConnectionState { }

[DigitalBrain.Neuron("shared", State = "shared")]
internal sealed class SharedConnectionNeuron()
    : ConnectionNeuron<SharedState>, IShared
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) => Task.FromResult(ConnectionProbeResult.Connected());

    // The refresh a shared connection's token needs; deterministic so a fact can assert the re-sealed value.
    protected override Task<OAuthGrant> RefreshAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(new OAuthGrant(Snapshot.AuthMethodId, "refreshed-access", "refresh-2", DateTimeOffset.UtcNow.AddHours(1), "read", null, "{}"));

    public Task<string> Use() => UseCredentialAsync((credential, _) => Task.FromResult(Key.Brain + ":" + credential.Value), CancellationToken.None);
}

// The fact's connection neurons are plain (06-08): this module's assembly [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module("shared-connector")]
public sealed class SharedConnectorNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
