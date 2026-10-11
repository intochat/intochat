using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.Equifax.Tests;

// Equifax on loopback, in the module's ASSUMED shape: a client-credentials token endpoint and the report route.
internal sealed class FakeEquifax : IAsyncDisposable
{
    public const string Report = """
        {"documentNumber":"12345678","infocorp":{"negative":true},"sbs":{"rating":"CPP","maxDaysDelay":45},
         "sicom":{"unpaidDebts":false},"products":{"mortgage":true,"carLoan":false}}
        """;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _clients = new();
    private readonly ConcurrentQueue<string> _bearers = new();
    private int _issued;

    private FakeEquifax(WebApplication app) { _app = app; }

    public Uri Origin => new(_app.Urls.Single() + "/");
    public bool RefuseToken { get; set; }

    // "id:secret" of every token request, from its basic auth header.
    public IReadOnlyList<string> TokenClients => [.. _clients];
    public IReadOnlyList<string> ReportBearers => [.. _bearers];

    public static async Task<FakeEquifax> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeEquifax(app);
        app.MapPost("/v2/oauth/token", (Delegate)fake.Token);
        app.MapGet("/business/consumer-credit/v1/reports", fake.Reports);
        await app.StartAsync(ct);
        return fake;
    }

    private async Task<IResult> Token(HttpContext http)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var basic = http.Request.Headers.Authorization.ToString();
        _clients.Enqueue(basic.StartsWith("Basic ", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])) : "");
        if (RefuseToken || form["grant_type"] != "client_credentials") { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
        var token = $"token-{Interlocked.Increment(ref _issued)}";
        return Results.Text($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":3600}""", "application/json");
    }

    private IResult Reports(HttpContext http)
    {
        _bearers.Enqueue(http.Request.Headers.Authorization.ToString());
        if (http.Request.Query["documentType"] != "DNI") { return Results.BadRequest(); }
        return Results.Text(Report.Replace("12345678", http.Request.Query["documentNumber"].ToString(), StringComparison.Ordinal), "application/json");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
