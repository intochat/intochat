namespace IntoChat.OS.Modules.Microsoft.Azure;

internal sealed record AppRevision(string Name, bool Active, int Replicas);

// The revisions of one container app, as the stop-then-start update sees them. Latest is the newest revision; LatestReady
// the newest one that became ready.
internal interface IAppRevisions
{
    Task<IReadOnlyList<AppRevision>> List(CancellationToken ct);

    Task<(string? Latest, string? LatestReady)> Heads(CancellationToken ct);

    Task Deactivate(string name, CancellationToken ct);

    Task Activate(string name, CancellationToken ct);
}

// Stop-then-start for an app whose silo must be alone in its cluster (ContainerAppSpec.StopBeforeUpdate). The running
// revisions are deactivated and drained before the update; the app is never left without a running revision:
// - an update that fails brings the stopped revisions back;
// - a new revision that does not become ready is deactivated and the stopped revisions come back;
// - an update that made no new revision (or an earlier run that stopped the app and went no further) activates the latest.
internal static class RevisionHandover
{
    public static async Task<T> StopThenUpdate<T>(
        IAppRevisions revisions,
        Func<Task<T>> update,
        int drainPolls,
        int readyPolls,
        Func<CancellationToken, Task> pause,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(pause);
        var stopped = new List<string>();
        T result;
        try
        {
            foreach (var revision in await revisions.List(ct))
            {
                if (!revision.Active) { continue; }
                stopped.Add(revision.Name);
                await revisions.Deactivate(revision.Name, ct);
            }
            // A drain that takes longer delays the update without breaking it: the new silo refuses to join while the old
            // one is live, and its container restarts until the old one is gone.
            for (var poll = 0; poll < drainPolls && (await revisions.List(ct)).Any(static revision => revision.Replicas > 0); poll++)
            {
                await pause(ct);
            }
            result = await update();
        }
        catch (Exception original)
        {
            await Restore(revisions, stopped, failed: null, original);
            throw;
        }

        await EnsureActive(revisions, ct);
        var (latest, _) = await revisions.Heads(ct);
        for (var poll = 0; poll < readyPolls && !await Ready(revisions, latest, ct); poll++)
        {
            await pause(ct);
        }
        if (!await Ready(revisions, latest, ct))
        {
            var refusal = new TimeoutException($"Revision {latest} did not become ready; the revisions stopped for it run again: {string.Join(", ", stopped)}.");
            await Restore(revisions, stopped, latest, refusal);
            throw refusal;
        }
        return result;
    }

    // A template change makes a new, active revision; an update that made none (or an earlier run that stopped the app and
    // went no further) leaves the latest revision to activate.
    public static async Task EnsureActive(IAppRevisions revisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if ((await revisions.List(ct)).Any(static revision => revision.Active)) { return; }
        if ((await revisions.Heads(ct)).Latest is { Length: > 0 } latest) { await revisions.Activate(latest, ct); }
    }

    private static async Task<bool> Ready(IAppRevisions revisions, string? latest, CancellationToken ct)
        => (await revisions.Heads(ct)).LatestReady is { } ready && string.Equals(ready, latest, StringComparison.Ordinal);

    // Best effort, after a failure: the failed revision goes, the stopped ones come back. A failure here is added to the
    // original one rather than hiding it.
    private static async Task Restore(IAppRevisions revisions, List<string> stopped, string? failed, Exception original)
    {
        List<Exception> failures = [];
        if (failed is not null && !stopped.Contains(failed, StringComparer.Ordinal))
        {
            try { await revisions.Deactivate(failed, CancellationToken.None); }
            catch (Exception again) { failures.Add(again); }
        }
        foreach (var name in stopped)
        {
            try { await revisions.Activate(name, CancellationToken.None); }
            catch (Exception again) { failures.Add(again); }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("The update failed and the stopped revisions could not all be activated again.", [original, .. failures]);
        }
    }
}
