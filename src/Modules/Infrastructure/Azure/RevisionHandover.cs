using global::Azure;

namespace IntoChat.OS.Modules.Microsoft.Azure;

internal sealed record AppRevision(string Name, bool Active, int Replicas);

// The revisions of one container app, as the stop-then-start update sees them. Latest is the newest revision; LatestReady
// the newest one that became ready.
internal abstract class AppRevisions
{
    public abstract Task<IReadOnlyList<AppRevision>> List(CancellationToken ct);

    public abstract Task<(string? Latest, string? LatestReady)> Heads(CancellationToken ct);

    public abstract Task Deactivate(string name, CancellationToken ct);

    public abstract Task Activate(string name, CancellationToken ct);

    public abstract Task EnsureMultipleMode(CancellationToken ct);

    public abstract Task SetTrafficToLatest(CancellationToken ct);

    public abstract Task SetTrafficToRevision(string revisionName, CancellationToken ct);
}

// Stop-then-start for an app whose silo must be alone in its cluster (ContainerAppSpec.StopBeforeUpdate). The app is first
// ensured to run in Multiple revision mode (so PUT ContainerApp will not automatically reactivate the old revision alongside
// the new one), then the running revisions are deactivated and drained before the update; the app is never left without a
// running revision:
// - an update that fails brings the stopped revisions back and restores ingress traffic;
// - a new revision that does not become ready is deactivated and drained before the stopped revisions come back with traffic pinned;
// - a new revision that becomes ready receives 100% traffic and any superseded active revisions are deactivated;
// - an update that made no new revision (or an earlier run that stopped the app and went no further) activates the latest.
internal static class RevisionHandover
{
    private const int RollbackDrainMaxPolls = 12;

    public static async Task<T> StopThenUpdate<T>(
        AppRevisions revisions,
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
            await revisions.EnsureMultipleMode(ct);
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
            await Restore(revisions, stopped, failed: null, original, drainPolls, pause);
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
            await Restore(revisions, stopped, latest, refusal, drainPolls, pause);
            throw refusal;
        }

        await revisions.SetTrafficToLatest(ct);
        foreach (var revision in await revisions.List(ct))
        {
            if (revision.Active && !string.Equals(revision.Name, latest, StringComparison.Ordinal))
            {
                await revisions.Deactivate(revision.Name, ct);
            }
        }
        return result;
    }

    // A template change makes a new, active revision; an update that made none (or an earlier run that stopped the app and
    // went no further) leaves the latest revision to activate.
    public static async Task EnsureActive(AppRevisions revisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if ((await revisions.List(ct)).Any(static revision => revision.Active)) { return; }
        if ((await revisions.Heads(ct)).Latest is { Length: > 0 } latest) { await revisions.Activate(latest, ct); }
    }

    private static async Task<bool> Ready(AppRevisions revisions, string? latest, CancellationToken ct)
        => (await revisions.Heads(ct)).LatestReady is { } ready && string.Equals(ready, latest, StringComparison.Ordinal);

    // Best effort, after a failure: the failed revision goes and drains, the stopped ones come back, and traffic pins to the
    // restored revision. An Azure refusal here is added to the original failure rather than hiding it.
    private static async Task Restore(
        AppRevisions revisions,
        List<string> stopped,
        string? failed,
        Exception original,
        int drainPolls,
        Func<CancellationToken, Task> pause)
    {
        List<Exception> failures = [];
        if (failed is not null && !stopped.Contains(failed, StringComparer.Ordinal))
        {
            try
            {
                await revisions.Deactivate(failed, CancellationToken.None);
                var maxRollbackPolls = Math.Min(Math.Max(drainPolls, 0), RollbackDrainMaxPolls);
                for (var poll = 0; poll < maxRollbackPolls; poll++)
                {
                    var current = await revisions.List(CancellationToken.None);
                    if (!current.Any(revision => string.Equals(revision.Name, failed, StringComparison.Ordinal) && revision.Replicas > 0))
                    {
                        break;
                    }
                    await pause(CancellationToken.None);
                }
            }
            catch (RequestFailedException again) { failures.Add(again); }
        }
        foreach (var name in stopped)
        {
            try { await revisions.Activate(name, CancellationToken.None); }
            catch (RequestFailedException again) { failures.Add(again); }
        }
        if (stopped.Count > 0)
        {
            try { await revisions.SetTrafficToRevision(stopped[^1], CancellationToken.None); }
            catch (RequestFailedException again) { failures.Add(again); }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("The update failed and the stopped revisions could not all be activated again.", [original, .. failures]);
        }
    }
}
