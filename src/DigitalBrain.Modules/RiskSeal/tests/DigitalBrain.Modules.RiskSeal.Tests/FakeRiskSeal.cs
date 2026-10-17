using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.RiskSeal.Tests;

// RiskSeal on loopback, answering the assumed wire shape the real provider calls.
internal sealed class FakeRiskSeal : IAsyncDisposable
{
    public const string Payload = """{"request_id":"rs-1","score":73.6,"risk_level":"high","signals":[{"name":"email_age_days","value":"3"},{"name":"social_profiles","value":0}]}""";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _bearers = new();
    private readonly ConcurrentQueue<JsonElement> _bodies = new();

    private FakeRiskSeal(WebApplication app) { _app = app; }

    public Uri BaseUrl => new(_app.Urls.Single() + "/");
    public string ValidKey { get; set; } = "deploy-key";
    public string Response { get; set; } = Payload;
    public IReadOnlyList<string> Bearers => [.. _bearers];
    public IReadOnlyList<JsonElement> Bodies => [.. _bodies];

    public static async Task<FakeRiskSeal> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeRiskSeal(app);
        app.MapPost("/v1/score", (Delegate)fake.Score);
        app.MapGet("/v1/account", fake.Account);
        await app.StartAsync(ct);
        return fake;
    }

    private async Task<IResult> Score(HttpContext http)
    {
        if (!Authorized(http)) { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
        using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        _bodies.Enqueue(body.RootElement.Clone());
        return Results.Text(Response, "application/json");
    }

    private IResult Account(HttpContext http)
        => Authorized(http) ? Results.Text("""{"plan":"sandbox"}""", "application/json") : Results.StatusCode(StatusCodes.Status401Unauthorized);

    private bool Authorized(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        _bearers.Enqueue(bearer);
        return bearer == ValidKey;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
