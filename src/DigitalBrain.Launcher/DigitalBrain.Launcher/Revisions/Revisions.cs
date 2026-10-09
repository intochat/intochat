using System.Net;
using DigitalBrain.Launcher.Processes;
using DigitalBrain.Launcher.Proxy;
using DigitalBrain.Launcher.Storage;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Launcher.Revisions;

// GET {mcp}/health of a revision; the launcher is the only thing allowed to poll a child for readiness.
public sealed class HealthProbe(Func<Uri, Task<HttpStatusCode>> probe)
{
    public static HealthProbe Http(HttpClient client) => new(async uri => (await client.GetAsync(uri)).StatusCode);
    public Task<HttpStatusCode> Probe(Uri uri) => probe(uri);
}

public sealed class Revisions(LauncherOptions options, IProcessRunner processes, IStorageCopier storage, RevisionProxy proxy, HealthProbe health, ILogger<Revisions> logger)
{
    private readonly Git _git = new(processes, options.Repository);
    private readonly Dictionary<string, IRunningProcess> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RevisionsDocument List() => RevisionsFile.Read(options.RevisionsPath);

    public async Task<Revision> ProposeAsync(string branch, string summary, IReadOnlyDictionary<string, string> files, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var staging = Path.Combine(options.Repository, ".revisions", "staging-" + Guid.NewGuid().ToString("N"));
            await _git.AddWorktree(staging, ct);
            foreach (var (relative, content) in files)
            {
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, content, ct);
            }
            await _git.Stage(staging, ct);
            await _git.Commit(staging, summary, ct);
            var sha = await _git.ShortSha(staging, ct);
            var worktree = Path.Combine(options.Repository, ".revisions", sha);
            Directory.Move(staging, worktree);
            await _git.Run(options.Repository, ct, "worktree", "repair", worktree);
            var document = List();
            var (ui, mcp) = document.NextBlock(options.FirstBlockPort, options.BlockSize);
            var revision = new Revision(sha, branch, worktree, ui, mcp, "created");
            RevisionsFile.Write(options.RevisionsPath, document.With(revision));
            return revision;
        }
        finally { _gate.Release(); }
    }

    public async Task<Revision> StartAsync(string sha, CancellationToken ct)
    {
        var revision = List().Require(sha);
        await storage.CopyContainerAsync(options.CurrentContainer, revision.Container, ct);
        _running[sha] = AspireRun.Start(processes, revision.Worktree, options.AppHostProject, revision);
        return Record(revision with { State = "starting" });
    }

    public async Task<Revision> WaitHealthyAsync(string sha, TimeSpan budget, CancellationToken ct)
    {
        var revision = List().Require(sha);
        var endpoint = new Uri(revision.McpEndpoint + "/health");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var exited = _running.TryGetValue(sha, out var process) ? process.Exited : Task.Delay(Timeout.Infinite, deadline.Token).ContinueWith(_ => -1, TaskScheduler.Default);
        while (!deadline.IsCancellationRequested)
        {
            if (exited.IsCompleted) { Record(revision with { State = "failed" }); throw new InvalidOperationException($"Revision {sha}'s aspire run exited with {exited.Result}."); }
            try { if (await health.Probe(endpoint) == HttpStatusCode.OK) { return Record(revision with { State = "healthy" }); } }
            catch (HttpRequestException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token); } catch (OperationCanceledException) { break; }
        }
        ct.ThrowIfCancellationRequested();
        Record(revision with { State = "failed" });
        throw new TimeoutException($"Revision {sha} did not answer {endpoint} within {budget}.");
    }

    public async Task<Revision> PromoteAsync(string sha, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var document = List();
            var revision = document.Require(sha);
            await _git.FastForward(revision.Branch, sha, ct);
            proxy.PointAt(revision.UiPort, revision.McpPort);
            var previous = document.Current;
            document = document.With(revision with { State = "current" }) with { Current = sha };
            RevisionsFile.Write(options.RevisionsPath, document);
            if (previous is not null && previous != sha) { await StopAsync(previous, ct); }
            return document.Require(sha);
        }
        finally { _gate.Release(); }
    }

    public async Task DiscardAsync(string sha, CancellationToken ct)
    {
        List().Require(sha);
        await StopAsync(sha, ct);
    }

    private async Task StopAsync(string sha, CancellationToken ct)
    {
        if (_running.Remove(sha, out var process))
        {
            try { await process.StopAsync(ct); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Revision {Sha} did not stop cleanly.", sha); }
            await process.DisposeAsync();
        }
        Record(List().Require(sha) with { State = "stopped" });
    }

    private Revision Record(Revision revision)
    {
        RevisionsFile.Write(options.RevisionsPath, List().With(revision));
        return revision;
    }
}
