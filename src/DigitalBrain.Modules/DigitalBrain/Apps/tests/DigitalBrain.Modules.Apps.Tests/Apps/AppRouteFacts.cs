using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Model;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// The whole apps surface: create is synchronous, implement answers 202 and finishes in the
// background, sharing rides export/import, and everything lives under the brain route.
public sealed class AppRouteFacts
{
    private const string Spec = """
        ## Scenario: It shouts
        Asking "hello" answers "HELLO!".
        """;

    private static async Task<ITestBrain> StartAuthoring(FakeModelServer model, CancellationToken ct)
    {
        var brain = await TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule>()
            .WithFakeModel(model)
            .WithFakeAppRuntime(new())
            .WithOptions<AppsModule, AppsOptions>(apps =>
            {
                apps.AuthorModel = nameof(IGpt56Luna);
                apps.BuilderModel = nameof(IGpt56Sol);
            })
            // The Builder's output is compile-checked by the sandbox; the fake accepts it.
            .ConfigureServices(services => services.AddSingleton<IScriptSandbox>(new RouteSandbox()))
            .StartAsync(ct);
        model.Script("gpt-5.6-luna", JsonSerializer.Serialize(new
        {
            name = "shouter",
            title = "Shouter",
            description = "Shouts back.",
            operations = new[] { new { name = "ask", description = "ask it" } },
            spec = Spec,
        }));
        model.Script("gpt-5.6-sol", JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, string> { ["synapses/shout.cs"] = "// b" },
        }));
        return brain;
    }

    [Fact]
    public async Task TheAppsSurfaceCreatesImplementsExportsAndDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAuthoring(model, ct);
        var alice = await brain.PersonAsync("alice", ct);
        var apps = $"/brains/{alice.BrainId}/apps";

        // Create is synchronous: the app exists, Drafted, when the call returns.
        using var created = await alice.Http.PostAsJsonAsync(apps, new { text = "Shout back." }, ct);
        var draft = await Json(created, ct);
        Assert.Equal("shouter", draft.GetProperty("name").GetString());
        Assert.Equal("Drafted", draft.GetProperty("status").GetString());

        // Implement answers 202 Building and the Builder lands in the background.
        await using var changes = await brain.Observe<AppChanged>(brain.Get<IApp>(IApp.Key(alice.BrainId, "shouter")), ct);
        using var implementing = await alice.Http.PostAsJsonAsync($"{apps}/shouter/implement", new { }, ct);
        Assert.Equal(HttpStatusCode.Accepted, implementing.StatusCode);
        Assert.Equal("Building", (await Json(implementing, ct)).GetProperty("status").GetString());
        await changes.NextAsync(change => change.Status == AppStatus.Live, ct);

        // A live version exports; the list shows the app; delete removes it.
        using var exported = await alice.Http.GetAsync($"{apps}/shouter/export", ct);
        Assert.Equal("shouter", (await Json(exported, ct)).GetProperty("name").GetString());
        Assert.Contains("shouter", await alice.Http.GetStringAsync(apps, ct), StringComparison.Ordinal);

        using var deleted = await alice.Http.DeleteAsync($"{apps}/shouter", ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.DoesNotContain("shouter", await alice.Http.GetStringAsync(apps, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestIsAcceptedAtOnceAndAnUnknownOperationIs422()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await StartAuthoring(model, ct);
        var alice = await brain.PersonAsync("alice", ct);
        var apps = $"/brains/{alice.BrainId}/apps";
        using var created = await alice.Http.PostAsJsonAsync(apps, new { text = "Shout back." }, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var accepted = await alice.Http.PostAsJsonAsync($"{apps}/shouter/request", new { operation = "ask", input = "hi" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(Guid.TryParse((await Json(accepted, ct)).GetProperty("requestId").GetString(), out _));

        using var unknown = await alice.Http.PostAsJsonAsync($"{apps}/shouter/request", new { operation = "nope", input = "" }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    [Fact]
    public async Task ImportOverAnExistingAppAnswers409()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var alice = await brain.PersonAsync("alice", ct);
        using (alice.AsCaller()) { await brain.Get<IApp>(IApp.Key(alice.BrainId, "red")).Create(new("Red", "red", [], [], []), "## Scenario: Red\nRed."); }

        using var clash = await alice.Http.PostAsJsonAsync($"/brains/{alice.BrainId}/apps/import",
            new { name = "red", version = "1.0.0", manifest = new { title = "Red", description = "", operations = Array.Empty<object>(), settings = Array.Empty<object>(), slots = Array.Empty<object>() }, spec = "x", files = new Dictionary<string, string>() }, ct);

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private sealed class RouteSandbox : IScriptSandbox
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
