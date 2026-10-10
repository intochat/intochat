using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Model;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Apps;

// IAppStudio is the Creator as one routed call: Build authors the app, waits for the Builder and
// answers it Live, so a router can route "build a calculator" to a single method.
public sealed class AppStudioFacts
{
    private const string Principal = "acct";
    private const string BrainName = "home";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;
    private const string Author = "gpt-5.6-luna";
    private const string Builder = "gpt-5.6-sol";

    [Fact]
    public async Task BuildAuthorsAndWaitsUntilTheAppIsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAsync(model, ct);
        model.Script(Author, Authored());
        model.Script(Builder, Built());
        Stamp();
        await brain.AuthorizeCallerAsync();

        var built = await brain.Get<IAppStudio>(Scope).Build("a calculator with a keypad", ct);

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

    private static Task<ModuleBrain> StartAsync(FakeModelServer model, CancellationToken ct) => ModuleTest.Create()
        .WithModule<AIModule>()
        .WithModule<AppsModule>()
        .WithFakeModel(model)
        .WithOptions<AppsModule, AppsOptions>(apps =>
        {
            apps.AuthorModel = nameof(IGpt56Luna);
            apps.BuilderModel = nameof(IGpt56Sol);
        })
        .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(new CreatorSandbox()))
        .StartAsync(ct);

    private static void Stamp() => CallerContextStamper.Stamp(new()
    { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

    private sealed class CreatorSandbox : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => Task.FromResult(new ScriptCompilationCheck(true, []));
    }
}
