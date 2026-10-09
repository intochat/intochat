using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Revisions;

internal sealed class HttpLauncherClient(IHttpClientFactory http, IOptions<RevisionsOptions> options) : ILauncherClient
{
    private sealed record Wire(string Sha, string Worktree, string McpEndpoint, string UiEndpoint, string State);

    private HttpClient Client()
    {
        var client = http.CreateClient("revisions.launcher");
        client.BaseAddress = new Uri(options.Value.LauncherUrl);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    public Task<LauncherRevision> Propose(string branch, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
        => Read(Client().PostAsJsonAsync("/revisions", new { branch, summary, files }, ct), ct);

    public Task<LauncherRevision> Start(string sha, CancellationToken ct) => Read(Client().PostAsync($"/revisions/{sha}/start", null, ct), ct);

    public async Task<LauncherRevision> WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        using var response = await Client().PostAsync($"/revisions/{sha}/healthy?budget={budget}", null, ct);
        if (response.StatusCode is HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway)
        { throw new TimeoutException(await response.Content.ReadAsStringAsync(ct)); }
        response.EnsureSuccessStatusCode();
        return ToRevision((await response.Content.ReadFromJsonAsync<Wire>(ct))!);
    }

    public Task<LauncherRevision> Promote(string sha, CancellationToken ct) => Read(Client().PostAsync($"/revisions/{sha}/promote", null, ct), ct);

    public async Task Discard(string sha, CancellationToken ct) => (await Client().DeleteAsync($"/revisions/{sha}", ct)).EnsureSuccessStatusCode();

    private static async Task<LauncherRevision> Read(Task<HttpResponseMessage> pending, CancellationToken ct)
    {
        using var response = await pending;
        if (!response.IsSuccessStatusCode)
        { throw new InvalidOperationException($"The launcher answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}"); }
        return ToRevision((await response.Content.ReadFromJsonAsync<Wire>(ct))!);
    }

    private static LauncherRevision ToRevision(Wire wire) => new(wire.Sha, wire.Worktree, wire.McpEndpoint, wire.UiEndpoint, wire.State);
}