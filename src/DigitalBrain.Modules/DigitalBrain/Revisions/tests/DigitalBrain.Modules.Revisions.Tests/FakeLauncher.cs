using DigitalBrain.Revisions;

namespace DigitalBrain.Modules.Revisions.Tests;

internal sealed class FakeLauncher : ILauncherClient
{
    public List<string> Calls { get; } = [];
    public bool Healthy { get; set; } = true;
    public Dictionary<string, int> SpanCounts { get; } = new(StringComparer.Ordinal);
    private int _shas;

    private static LauncherRevision At(string sha, string state) => new(sha, @"E:\wt\" + sha, "http://localhost:5091", "http://localhost:5090", state);

    public Task<LauncherRevision> Propose(string branch, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        var sha = "sha" + (++_shas).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        Calls.Add("propose " + sha);
        return Task.FromResult(At(sha, "created"));
    }

    public Task<LauncherRevision> Start(string sha, CancellationToken ct) { Calls.Add("start " + sha); return Task.FromResult(At(sha, "starting")); }

    public Task<LauncherRevision> WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        Calls.Add("healthy " + sha);
        return Healthy ? Task.FromResult(At(sha, "healthy")) : Task.FromException<LauncherRevision>(new TimeoutException($"Revision {sha} did not answer within {budget}."));
    }

    public Task<LauncherRevision> Promote(string sha, CancellationToken ct) { Calls.Add("promote " + sha); return Task.FromResult(At(sha, "current")); }
    public Task Discard(string sha, CancellationToken ct) { Calls.Add("discard " + sha); return Task.CompletedTask; }
    public Task<int> Spans(string mcpEndpoint, string name, CancellationToken ct) => Task.FromResult(SpanCounts.GetValueOrDefault(name));
}