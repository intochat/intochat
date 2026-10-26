using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using IntoChat.OS.Testing;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Apps;

// IAppStudio is the Creator as one routed call: Build authors the app, waits for the Builder and
// answers it Live, so a router can route "build a calculator" to a single method.
public sealed class AppStudioFacts
{
    private const string Author = "gpt-5.6-luna";
    private const string Builder = "gpt-5.6-sol";

    [Trait("host", "own")]
    [Fact]
    public async Task BuildAuthorsAndWaitsUntilTheAppIsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored());
        model.Script(Builder, Built());
        var alice = await brain.PersonAsync("alice", ct);

        using var _ = alice.AsCaller();
        var built = await brain.Get<IAppStudio>(IAppStudio.Key(alice.BrainId)).Build("a calculator with a keypad", ct);

        Assert.Equal(AppStatus.Live, built.Status);
        Assert.Equal("Calculator", built.Manifest.Title);
        Assert.Contains("synapses/calculator.cs", built.Files.Keys);
    }

    private static string Authored() => System.Text.Json.JsonSerializer.Serialize(new
    {
        name = "calculator",
        title = "Calculator",
        description = "A keypad that evaluates arithmetic.",
        operations = new[] { new { name = "ask", description = "evaluate" } },
        settings = Array.Empty<object>(),
        spec = "# Calculator\n\nA keypad.",
    });

    private static string Built() => System.Text.Json.JsonSerializer.Serialize(new
    {
        files = new Dictionary<string, string> { ["synapses/calculator.cs"] = "// keypad" },
    });

    private static Task<ITestBrain> StartAsync(FakeModelServer model, CancellationToken ct) => TestBrain.Create()
        .WithModule<AIModule>()
        .WithModule<AppsModule>()
        .WithFakeModel(model)
        .WithOptions<AppsModule, AppsOptions>(apps =>
        {
            apps.AuthorModel = nameof(IGpt56Luna);
            apps.BuilderModel = nameof(IGpt56Sol);
        })
        // The Builder's output is compile-checked by the sandbox; the fake accepts it.
        .ConfigureServices(services => services.AddSingleton<IScriptSandbox>(new CreatorSandbox()))
        .StartAsync(ct);

    private sealed class CreatorSandbox : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken)
            => ReadContracts([moduleId], cancellationToken);

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => Task.FromResult(new ScriptCompilationCheck(true, []));
    }
}
