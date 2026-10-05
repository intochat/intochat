using System.Text;
using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Scripted;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// The whole apps surface: create is synchronous, implement and verify answer 202 and finish
// in the background, sharing rides export/import, and everything lives under the brain route.
public sealed class AppRouteFacts
{
    private const string Spec = """
        ## Scenario: It shouts
        Asking "hello" answers "HELLO!".
        """;

    [Fact(Timeout = 120_000)]
    public async Task TheAppsSurfaceCreatesImplementsExportsAndDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ScriptedTestRunner();
        runner.BySourceMarker["// proof v1"] = (0, "dbtest:pass It shouts");
        await using var brain = await ModuleTest.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule>()
            .ConfigureModule<AppsModule, AppsOptions>(apps =>
            {
                apps.AuthorModel = IScriptedLLM.ModelPrefix + "author";
                apps.BuilderModel = IScriptedLLM.ModelPrefix + "builder";
            })
            .ConfigureSilo(silo =>
            {
                silo.Services.AddSingleton<ITestScriptRunner>(runner);
                silo.Services.AddSingleton<IScriptSandbox>(new RouteSandbox());
            })
            .StartAsync(ct);
        await brain.Get<IScriptedLLM>("author").Script([JsonSerializer.Serialize(new
        {
            name = "shouter",
            title = "Shouter",
            description = "Shouts back.",
            operations = new[] { new { name = "ask", description = "ask it" } },
            spec = Spec,
        })]);
        await brain.Get<IScriptedLLM>("builder").Script([JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, string> { ["tests.cs"] = "// proof v1", ["behaviors/shout.cs"] = "// b" },
        })]);

        var log = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<global::Microsoft.Extensions.Logging.ILoggerProvider>(new CapturingLoggerProvider(log));
        builder.Services.AddSingleton<IDigitalBrain>(brain.SiloServices.GetRequiredService<IDigitalBrain>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<MarketplaceService>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<Creator>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<AppWorkQueue>());
        builder.Services.AddSingleton<IBrainAccess, OpenBrainAccess>();
        await using var edge = builder.Build();
        new AppsModule().Configure(edge);
        var endpoints = ((IEndpointRouteBuilder)edge).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var scope = BrainScope.Create("account-alice", "alice").Id;

        // Create is synchronous: the app exists, Drafted, when the call returns.
        var created = await Invoke("POST", "/brains/{brainId}/apps/", new { text = "Shout back." }, ("brainId", "alice"));
        Assert.True(created.Response.StatusCode == 200, created.Response.StatusCode + ": " + created.Body + " | " + string.Join(" || ", log));
        Assert.Equal("shouter", Json(created).GetProperty("name").GetString());
        Assert.Equal("Drafted", Json(created).GetProperty("status").GetString());

        // Implement answers 202 Building and the Builder lands in the background.
        var implementing = await Invoke("POST", "/brains/{brainId}/apps/{name}/implement", new { }, ("brainId", "alice"), ("name", "shouter"));
        Assert.Equal(202, implementing.Response.StatusCode);
        Assert.Equal("Building", Json(implementing).GetProperty("status").GetString());
        var app = brain.Get<IAppV2>(scope + "/apps/shouter");
        await WaitFor(app, AppStatusV2.Live, ct);

        // Verify answers 202 and the verdicts land in the background.
        var verifying = await Invoke("POST", "/brains/{brainId}/apps/{name}/verify", new { }, ("brainId", "alice"), ("name", "shouter"));
        Assert.Equal(202, verifying.Response.StatusCode);
        await WaitForGreen(app, ct);

        // A green version exports; the list shows the app; delete removes it.
        var exported = await Invoke("GET", "/brains/{brainId}/apps/{name}/export", null, ("brainId", "alice"), ("name", "shouter"));
        Assert.Equal(200, exported.Response.StatusCode);
        Assert.Equal("shouter", Json(exported).GetProperty("name").GetString());

        var listed = await Invoke("GET", "/brains/{brainId}/apps/", null, ("brainId", "alice"));
        Assert.Equal(200, listed.Response.StatusCode);
        Assert.Contains("shouter", listed.Body, StringComparison.Ordinal);

        var deleted = await Invoke("DELETE", "/brains/{brainId}/apps/{name}", null, ("brainId", "alice"), ("name", "shouter"));
        Assert.Equal(200, deleted.Response.StatusCode);
        var relisted = await Invoke("GET", "/brains/{brainId}/apps/", null, ("brainId", "alice"));
        Assert.DoesNotContain("shouter", relisted.Body, StringComparison.Ordinal);

        async Task<(HttpResponse Response, string Body)> Invoke(string method, string pattern, object? body, params (string Key, string Value)[] values)
        {
            CallerContextStamper.Stamp(new CallerContext
            {
                PrincipalId = "alice",
                AccountId = "account-alice",
                BrainId = "alice",
                Kind = CallerKind.User,
                StampedBy = TrustedEdge.AuthenticatedHttp,
            });
            var endpoint = endpoints.Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var http = new DefaultHttpContext { RequestServices = edge.Services, RequestAborted = ct };
            http.Request.Method = method;
            http.Request.Headers.Authorization = "Basic test";
            foreach (var (key, value) in values) { http.Request.RouteValues[key] = value; }
            if (body is not null)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                http.Request.ContentType = "application/json";
                http.Request.Body = new MemoryStream(payload);
                http.Request.ContentLength = payload.Length;
                http.Features.Set<global::Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
            }
            http.Response.Body = new MemoryStream();
            http.SetEndpoint(endpoint);
            await endpoint.RequestDelegate!(http);
            http.Response.Body.Position = 0;
            return (http.Response, await new StreamReader(http.Response.Body).ReadToEndAsync(ct));
        }

        static JsonElement Json((HttpResponse Response, string Body) result)
            => JsonDocument.Parse(result.Body).RootElement.Clone();
    }

    [Fact]
    public async Task ExportOfARedAppAndImportClashesAnswer409()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var scope = BrainScope.Create("account-alice", "alice").Id;
        var app = brain.Get<IAppV2>(scope + "/apps/red");
        await app.Create(new("Red", "red", [], [], []), "## Scenario: Red\nRed.");

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDigitalBrain>(brain.SiloServices.GetRequiredService<IDigitalBrain>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<MarketplaceService>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<Creator>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<AppWorkQueue>());
        builder.Services.AddSingleton<IBrainAccess, OpenBrainAccess>();
        await using var edge = builder.Build();
        new AppsModule().Configure(edge);
        var endpoints = ((IEndpointRouteBuilder)edge).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        var exported = await Invoke("GET", "/brains/{brainId}/apps/{name}/export", null, ("brainId", "alice"), ("name", "red"));
        exported.Body.Position = 0;
        Assert.True(exported.StatusCode == 409, $"{exported.StatusCode}: {await new StreamReader(exported.Body).ReadToEndAsync(ct)}");

        var clash = await Invoke("POST", "/brains/{brainId}/apps/import",
            new { name = "red", version = "1.0.0", manifest = new { title = "Red", description = "", operations = Array.Empty<object>(), settings = Array.Empty<object>(), slots = Array.Empty<object>() }, spec = "x", files = new Dictionary<string, string>() },
            ("brainId", "alice"));
        Assert.Equal(409, clash.StatusCode);

        async Task<HttpResponse> Invoke(string method, string pattern, object? body, params (string Key, string Value)[] values)
        {
            CallerContextStamper.Stamp(new CallerContext
            {
                PrincipalId = "alice",
                AccountId = "account-alice",
                BrainId = "alice",
                Kind = CallerKind.User,
                StampedBy = TrustedEdge.AuthenticatedHttp,
            });
            var endpoint = endpoints.Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var http = new DefaultHttpContext { RequestServices = edge.Services, RequestAborted = ct };
            http.Request.Method = method;
            http.Request.Headers.Authorization = "Basic test";
            foreach (var (key, value) in values) { http.Request.RouteValues[key] = value; }
            if (body is not null)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                http.Request.ContentType = "application/json";
                http.Request.Body = new MemoryStream(payload);
                http.Request.ContentLength = payload.Length;
                http.Features.Set<global::Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());
            }
            http.Response.Body = new MemoryStream();
            http.SetEndpoint(endpoint);
            await endpoint.RequestDelegate!(http);
            return http.Response;
        }
    }

    private static async Task WaitFor(IAppV2 app, AppStatusV2 status, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while ((await app.Read()).Status != status) { await Task.Delay(50, deadline.Token); }
    }

    private static async Task WaitForGreen(IAppV2 app, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await app.Read();
            if (snapshot.Versions.Any(version => version.Version == snapshot.Version && version.Green == true)) { return; }
            await Task.Delay(50, deadline.Token);
        }
    }

    private sealed class CapturingLoggerProvider(List<string> log) : global::Microsoft.Extensions.Logging.ILoggerProvider
    {
        public global::Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);
        public void Dispose() { }
        private sealed class Logger(List<string> log, string category) : global::Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(global::Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(global::Microsoft.Extensions.Logging.LogLevel logLevel, global::Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => log.Add(category + ": " + formatter(state, exception) + (exception is null ? "" : " EX: " + exception.Message));
        }
    }

    private sealed class HasBody : global::Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class OpenBrainAccess : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class RouteSandbox : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "csharp uses sandbox contracts.";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public ScriptCompilationCheck Check(IReadOnlyDictionary<string, string> files) => new(true, []);
    }
}
