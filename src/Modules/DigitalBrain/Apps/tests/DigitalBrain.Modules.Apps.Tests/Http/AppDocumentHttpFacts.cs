using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests.Http;

// The Creator's document lives on the live app and belongs to its author.
public sealed class AppDocumentHttpFacts
{
    [Fact(Timeout = 120_000)]
    public async Task CreatorDocumentsBelongToTheirAuthorOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(new RunnableSandbox()))
            .StartAsync(ct);
        var log = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<global::Microsoft.Extensions.Logging.ILoggerProvider>(new ListLoggerProvider(log));
        builder.Services.AddSingleton<IDigitalBrain>(brain.SiloServices.GetRequiredService<IDigitalBrain>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<MarketplaceService>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<CreatorService>());
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<LiveAppPackaging>());
        builder.Services.AddSingleton<IBrainAccess, PersonalBrainAccess>();
        await using var app = builder.Build();
        new AppsModule().Configure(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        Stamp("alice");
        var created = brain.Get<IApp>(BrainScope.Create("account-alice", "alice").Id + "/packages/alice/blocks");
        // The grain claims the creator's ambient scope the moment it is first addressed.
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "alice", AccountId = "account-alice", BrainId = "alice",
            Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        await created.Create(new(Guid.NewGuid(), "Blocks", "Small behaviors.", "prompt"));

        var probe = await Invoke("GET", "/brains/{brainId}/packages/{owner}/{name}/creator/content", "alice", null,
            ("brainId", "alice"), ("owner", "alice"), ("name", "blocks"));
        Assert.True(probe.Response.StatusCode == 200, "content probe: " + probe.Response.StatusCode);

        // Ownership guards the whole Creator group: bob cannot even read alice's Creator.
        var denied = await Invoke("GET", "/brains/{brainId}/packages/{owner}/{name}/creator/content", "bob", null,
            ("brainId", "bob"), ("owner", "alice"), ("name", "blocks"));
        Assert.Equal(403, denied.Response.StatusCode);

        async Task<DefaultHttpContext> Invoke(string method, string pattern, string principal, object? body, params (string Key, string Value)[] values)
        {
            Stamp(principal);
            var endpoint = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == pattern
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
            var http = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = ct };
            http.Request.Method = method;
            http.Request.Headers.Authorization = "Basic test";
            if (body is not null)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                http.Request.Body = new MemoryStream(payload);
                http.Request.ContentType = "application/json";
                http.Request.ContentLength = payload.Length;
            }
            foreach (var (key, value) in values) { http.Request.RouteValues[key] = value; }
            http.Response.Body = new MemoryStream();
            http.SetEndpoint(endpoint);
            try { await endpoint.RequestDelegate!(http); }
            catch (UnauthorizedAccessException) { http.Response.StatusCode = 403; }
            return http;
        }

        static void Stamp(string principal) => CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = principal,
            AccountId = "account-" + principal,
            BrainId = principal,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
    }

    private sealed class ListLoggerProvider(List<string> log) : global::Microsoft.Extensions.Logging.ILoggerProvider
    {
        public global::Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(log, categoryName);
        public void Dispose() { }
        private sealed class ListLogger(List<string> log, string category) : global::Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(global::Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= global::Microsoft.Extensions.Logging.LogLevel.Debug;
            public void Log<TState>(global::Microsoft.Extensions.Logging.LogLevel logLevel, global::Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => log.Add(category + ": " + formatter(state, exception) + (exception is null ? "" : " :: " + exception.Message));
        }
    }

    private sealed class PersonalBrainAccess : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(principalId == brainId);
    }

    private sealed class RunnableSandbox : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "Test sandbox";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public ScriptCompilationCheck Check(IReadOnlyDictionary<string, string> files) => new(true, []);
    }
}
