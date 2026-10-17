using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.Twilio.Tests;

// Twilio's REST API on loopback: the Messages resource and the Account resource the probe reads.
internal sealed class FakeTwilio : IAsyncDisposable
{
    public enum Mode { Accept, Unauthorized, InvalidNumber, Down }

    public sealed record Request(string Path, string Authorization, IReadOnlyDictionary<string, string> Form);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Request> _requests = new();
    private int _sequence;

    private FakeTwilio(WebApplication app) { _app = app; }

    public Uri Origin => new(_app.Urls.Single() + "/");
    public Mode Answer { get; set; } = Mode.Accept;
    public IReadOnlyList<Request> Requests => [.. _requests];
    public IReadOnlyList<Request> Sends => [.. _requests.Where(r => r.Path.EndsWith("/Messages.json", StringComparison.Ordinal))];

    public static string Basic(string sid, string token) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{token}"));

    public static async Task<FakeTwilio> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeTwilio(app);
        app.MapPost("/2010-04-01/Accounts/{sid}/Messages.json", fake.Messages);
        app.MapGet("/2010-04-01/Accounts/{file}", fake.Account);
        await app.StartAsync(ct);
        return fake;
    }

    private async Task<IResult> Messages(HttpContext http, string sid)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        _requests.Enqueue(new(http.Request.Path, http.Request.Headers.Authorization.ToString(), form.ToDictionary(f => f.Key, f => f.Value.ToString())));
        return Answer switch
        {
            Mode.Unauthorized => Results.Text("""{"code":20003,"message":"Authenticate","status":401}""", "application/json", statusCode: 401),
            Mode.InvalidNumber => Results.Text("""{"code":21211,"message":"The 'To' number is not a valid phone number.","status":400}""", "application/json", statusCode: 400),
            Mode.Down => Results.StatusCode(503),
            _ => Results.Text($$"""{"sid":"SM{{Interlocked.Increment(ref _sequence):D4}}","status":"queued","error_code":null,"to":"{{form["To"]}}"}""", "application/json", statusCode: 201),
        };
    }

    private IResult Account(HttpContext http, string file)
    {
        _requests.Enqueue(new(http.Request.Path, http.Request.Headers.Authorization.ToString(), new Dictionary<string, string>()));
        return Answer switch
        {
            Mode.Unauthorized => Results.Text("""{"code":20003,"status":401}""", "application/json", statusCode: 401),
            Mode.Down => Results.StatusCode(503),
            _ => Results.Text($$"""{"sid":"{{file.Replace(".json", "", StringComparison.Ordinal)}}","status":"active"}""", "application/json"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
