using System.Net;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A real brain, its real HTTP runtime edge and the real sandbox Session joined in process: assignments reach the session over its
// stream, and assemblies are served from the brain's own store. Programs here are compiled by the facts, then assigned prebuilt.
internal sealed class PrebuiltRig : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _control;

    private PrebuiltRig(ITestBrain brain, MemoryBehaviorAssemblies store, SandboxOptions options)
    {
        Brain = brain;
        Store = store;
        Options = options;
    }

    public ITestBrain Brain { get; }
    public MemoryBehaviorAssemblies Store { get; }
    public SandboxOptions Options { get; }
    public string Scope => Brain.Owner.BrainId;
    public string AppKey => IApp.Key(Scope, "counter");
    public IBrainPrograms Programs => RuntimeBrain.Programs(Brain, Scope);
    public IApp App => Brain.Get<IApp>(AppKey);
        public Session? Session { get; private set; }
    public SessionConnection? Connection { get; private set; }

    public static async Task<PrebuiltRig> Start(CancellationToken ct, Action<SandboxOptions>? sandbox = null)
    {
        var store = new MemoryBehaviorAssemblies();
        var brain = await RuntimeBrain.Create(new FakeSessionHost(), apps: true)
            .ConfigureServices(services => services.AddSingleton<IBehaviorAssemblies>(store)).StartAsync(ct);
        var options = new SandboxOptions { CacheRoot = Path.Combine(Path.GetTempPath(), "prebuilt-behaviors-" + Guid.NewGuid().ToString("N")) };
        sandbox?.Invoke(options);
        var rig = new PrebuiltRig(brain, store, options);
        using (brain.Owner.AsCaller())
        { await rig.App.Create(new("Counter", "", [new("add", ""), new("pong", "")], [], []), "spec"); }
        return rig;
    }

    // An "add" request: the behavior answers with a "pong" request carrying the input plus an offset only this build knows.
    public CompiledBehavior Build(int offset, string extra = "")
    {
        var compilation = Brain.Services.GetRequiredService<CSharpScriptCheck>().Compile(new Dictionary<string, string>
        {
            ["add.cs"] = $$"""
                using IntoChat.OS.Modules.Apps;
                await using var brain = await IntoChatOSClient.ConnectAsync(args);
                var app = brain.Get<IApp>(brain.Setting("App")!);
                {{extra}}
                await foreach (var request in brain.On(app, "add", brain.Stopping))
                {
                    await app.Request("pong", (int.Parse(request.Input) + {{offset}}).ToString());
                }
                """,
        });
        Assert.True(compilation.Success, string.Join("; ", compilation.Errors));
        return compilation.Assemblies["add.cs"];
    }

    public Dictionary<string, string> Settings => new(StringComparer.Ordinal) { ["App"] = AppKey };

    public static Dictionary<string, PrebuiltBehavior> One(CompiledBehavior built)
        => new(StringComparer.Ordinal) { ["add.cs"] = new(built.Assembly, built.Symbols, built.Hash) };

    public Task<ProgramView> Assign(CompiledBehavior built) => Programs.AssignPrebuilt(AppKey, One(built), Settings);

    // Opens the real sandbox Session once the first assignment made the brain start one.
    public async Task Attach(CancellationToken ct, ILogger? logger = null)
    {
        var id = (await RuntimeBrain.Runtime(Brain, Scope).Inspect(ct)).SessionId!;
        var token = Brain.Services.GetRequiredService<SessionTokens>().Issue(Scope, id);
        Connection = await SessionConnection.OpenAsync(new Uri("http://brain.test/brains/" + Scope), token, RuntimeBrain.Fingerprint(Brain), new Forward(Brain.HttpClient), ct);
        var loader = new BehaviorLoader(new HttpClient(new Memory(Store)), global::Microsoft.Extensions.Options.Options.Create(Options));
        Session = new Session(Connection, loader, logger ?? NullLogger.Instance);
        var session = Session;
        _control = Task.Run(() => session.RunAsync(_stop.Token), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_control is not null) { await _control; } } catch (OperationCanceledException) { }
        if (Session is not null) { await Session.DisposeAsync(); }
        if (Connection is not null) { await Connection.DisposeAsync(); }
        await Brain.DisposeAsync();
    }

    private sealed class Memory(MemoryBehaviorAssemblies store) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.AbsolutePath.Trim('/');
            var symbols = name.EndsWith(".pdb", StringComparison.Ordinal);
            var hash = symbols ? name[..^4] : name;
            return Task.FromResult(store.Stored.TryGetValue(hash, out var found)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(symbols ? found.Symbols : found.Assembly) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Forward(HttpClient brain) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery) { Content = request.Content };
            foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            return brain.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }
}
