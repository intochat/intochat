using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.Monnet.Tests;

// Monnet on loopback, answering the module's assumed REST shape. A key outside Keys answers 401.
internal sealed class FakeMonnet : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<(string Path, string ApiKey, string MerchantId)> _requests = new();
    private int _next;

    private FakeMonnet(WebApplication app) { _app = app; }

    public Uri Origin => new(_app.Urls.Single() + "/");
    public ConcurrentDictionary<string, bool> Keys { get; } = new(new Dictionary<string, bool> { ["deployment-key"] = true, ["pasted-key"] = true });
    public IReadOnlyList<(string Path, string ApiKey, string MerchantId)> Requests => [.. _requests];

    public static async Task<FakeMonnet> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeMonnet(app);
        app.MapPost("/v1/payments", (Delegate)fake.Create);
        app.MapGet("/v1/payments/{id}", fake.Read);
        app.MapGet("/v1/merchants/me", fake.Me);
        await app.StartAsync(ct);
        return fake;
    }

    private bool Admit(HttpContext http)
    {
        var key = http.Request.Headers["X-Api-Key"].ToString();
        _requests.Enqueue((http.Request.Path, key, http.Request.Headers["X-Merchant-Id"].ToString()));
        return Keys.ContainsKey(key);
    }

    private async Task<IResult> Create(HttpContext http)
    {
        if (!Admit(http)) { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
        using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var id = $"pay-{Interlocked.Increment(ref _next)}";
        return Results.Json(new
        {
            id,
            reference = body.RootElement.GetProperty("reference").GetString(),
            amount = body.RootElement.GetProperty("amount").GetDecimal(),
            currency = body.RootElement.GetProperty("currency").GetString(),
            status = "created",
            checkoutUrl = $"https://checkout.example/{id}",
        });
    }

    private IResult Read(HttpContext http, string id)
        => Admit(http) ? Results.Json(new { id, reference = "loan-1", amount = 120.5m, currency = "PEN", status = "paid" }) : Results.StatusCode(StatusCodes.Status401Unauthorized);

    private IResult Me(HttpContext http) => Admit(http) ? Results.Json(new { id = "merchant" }) : Results.StatusCode(StatusCodes.Status401Unauthorized);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
