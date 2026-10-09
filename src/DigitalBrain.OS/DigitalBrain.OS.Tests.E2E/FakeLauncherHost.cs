using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DigitalBrain.OS.Tests.E2E;

// A launcher that "starts" a child by answering the parent's own MCP endpoint, so verification
// drives real neurons; promote and discard are recorded.
public sealed class FakeLauncherHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public List<string> Calls { get; } = [];
    public string Url => _app.Urls.Single();

    private FakeLauncherHost(WebApplication app) => _app = app;

    public static async Task<FakeLauncherHost> StartAsync(string childMcpEndpoint, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var host = new FakeLauncherHost(app);
        object Revision(string sha, string state) => new { sha, worktree = @"E:\wt\" + sha, mcpEndpoint = childMcpEndpoint, uiEndpoint = childMcpEndpoint, state };
        app.MapPost("/revisions", (HttpContext _) => { host.Calls.Add("propose"); return Results.Created("/revisions/e2e0001", Revision("e2e0001", "created")); });
        app.MapPost("/revisions/{sha}/start", (string sha) => { host.Calls.Add("start " + sha); return Results.Ok(Revision(sha, "starting")); });
        app.MapPost("/revisions/{sha}/healthy", (string sha) => { host.Calls.Add("healthy " + sha); return Results.Ok(Revision(sha, "healthy")); });
        app.MapPost("/revisions/{sha}/promote", (string sha) => { host.Calls.Add("promote " + sha); return Results.Ok(Revision(sha, "current")); });
        app.MapDelete("/revisions/{sha}", (string sha) => { host.Calls.Add("discard " + sha); return Results.NoContent(); });
        app.MapGet("/revisions/spans", () => Results.Ok(1));
        await app.StartAsync(ct);
        return host;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}