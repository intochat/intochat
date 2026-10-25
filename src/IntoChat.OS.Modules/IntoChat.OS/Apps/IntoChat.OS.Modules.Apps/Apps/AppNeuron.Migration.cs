using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task Pause(string migration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migration);
        if (Snapshot.Migration is { } existing && existing != migration)
        { throw new InvalidOperationException("Another app migration is already recorded."); }
        if (Snapshot.Migration is null)
        {
            var next = Snapshot.Clone();
            next.MigrationArchive = Snapshot.Clone();
            next.Migration = migration;
            next.MigrationPaused = true;
            next.Synapses = [.. next.Synapses.Select(entry => entry.Status == SynapseStatus.Requested
                ? entry with { Status = SynapseStatus.Unsettled, Errors = ["App migration retired the old program; the staged source is retained for review."] }
                : entry)];
            await Persist(next);
        }
        else if (Snapshot.Settings.GetValueOrDefault("CompletedAppMigration") != migration && !Snapshot.MigrationPaused)
        {
            var next = Snapshot.Clone();
            next.MigrationPaused = true;
            await Persist(next);
        }
        // Retrying retirement is necessary if its acknowledgement was lost. The persisted gate
        // also prevents activation from assigning this old program again.
        if (Snapshot.MigrationPaused && Runtime is { } runtime)
        { await runtime.Retire(this.GetPrimaryKeyString(), CancellationToken.None); }
    }

    public Task<AppState> Archive(string migration)
    {
        CheckMigration(migration);
        return Task.FromResult(Snapshot.MigrationArchive!.Clone());
    }

    public async Task Adopt(string migration, string source)
    {
        CheckMigration(migration);
        CheckSibling(source);
        if (Snapshot.MigrationSource is { } adopted)
        {
            if (adopted != source) { throw new InvalidOperationException("This migration already adopted another source."); }
            return;
        }
        var original = await GrainFactory.GetGrain<IAppMigration>(source).Archive(migration);
        var next = Snapshot.Clone();
        foreach (var (key, value) in original.Settings) { next.PutSetting(key, value); }
        foreach (var (key, value) in original.Connections) { next.PutConnection(key, value); }
        next.PutSetting("ChatDataApp", source);
        next.Principal ??= original.Principal;
        next.MigrationSource = source;
        AddRequests(next, original.RequestRetention);
        // Keep both original app archives intact, including custom sources and all receipts.
        await Persist(next);
    }

    public async Task Redirect(string migration, string target)
    {
        CheckMigration(migration);
        CheckSibling(target);
        if (Snapshot.MigrationTarget is { } existing && existing != target)
        { throw new InvalidOperationException("This migration already redirects elsewhere."); }
        var next = Snapshot.Clone();
        next.MigrationTarget = target;
        await Persist(next);
        // Include requests accepted while the source was paused. New requests now go directly
        // to the target; replay of this transfer is deduplicated by original request identity.
        await GrainFactory.GetGrain<IAppMigration>(target).MergeRequests(Snapshot.RequestRetention);
    }

    public async Task Resume(string migration)
    {
        CheckMigration(migration);
        if (Snapshot.MigrationTarget is not null) { throw new InvalidOperationException("An alias cannot run its retired program."); }
        var next = Snapshot.Clone();
        next.MigrationPaused = false;
        next.Status = AppStatus.Deploying;
        await Persist(next);
        try { await Deploy(); }
        catch
        {
            var failed = Snapshot.Clone();
            failed.Status = AppStatus.Failed;
            await Persist(failed);
            throw;
        }
        next = Snapshot.Clone();
        next.Status = AppStatus.Live;
        next.PutSetting("CompletedAppMigration", migration);
        await Persist(next);
    }

    public async Task MergeRequests(RetainedSignal[] requests)
    {
        if (Snapshot.Migration is null) { throw new InvalidOperationException("Request adoption requires a migration."); }
        foreach (var request in requests.Select(item => item.Signal).OfType<AppRequested>())
        {
            await Accept(request.Operation, request.Input, request.RequestId);
            if (Snapshot.MigrationRequests.Any(held => held.RequestId == request.RequestId)) { continue; }
            var next = Snapshot.Clone();
            next.MigrationRequests = [.. next.MigrationRequests, request];
            await Persist(next);
        }
    }

    private static void AddRequests(AppState next, RetainedSignal[] requests)
    {
        foreach (var request in requests.Select(item => item.Signal).OfType<AppRequested>())
        {
            if (next.RequestRetention.Any(item => item.Signal is AppRequested held && held.RequestId == request.RequestId)) { continue; }
            next.RequestSequence++;
            next.RequestRetention = [.. next.RequestRetention, new(next.RequestSequence, request)];
            next.MigrationRequests = [.. next.MigrationRequests, request];
        }
    }

    public async Task Accept(string operation, string input, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaxPayloadLength) { throw new ArgumentException("A request input is at most 64 KiB."); }
        if (requestId == Guid.Empty) { throw new ArgumentException("A request identity is required."); }
        var prior = Snapshot.RequestRetention.Select(item => item.Signal).OfType<AppRequested>()
            .Concat(Snapshot.MigrationRequests).FirstOrDefault(item => item.RequestId == requestId);
        if (prior is not null)
        {
            if (prior.Operation != operation || prior.Input != input)
            { throw new InvalidOperationException("A request identity cannot be reused for another command."); }
            return;
        }
        var request = new AppRequested(operation, input, requestId) with { Publisher = Id };
        if (!Snapshot.MigrationPaused)
        {
            if (Snapshot.Manifest is null || !Snapshot.Manifest.Operations.Any(candidate => candidate.Name == operation))
            { throw new ArgumentException($"{operation} is not an operation of {Name()}."); }
            if (await Rejection(request) is { } rejected)
            { throw new ArgumentException($"{rejected.Rule} refused {operation}: {rejected.Reason}"); }
        }
        var next = Snapshot.Clone();
        next.RequestSequence++;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(2 * RetainedCapacity - 1), new(next.RequestSequence, request)];
        await Save(next, new AppRequested(operation, input, requestId));
    }

    private void CheckMigration(string migration)
    {
        if (Snapshot.Migration != migration || Snapshot.MigrationArchive is null)
        { throw new InvalidOperationException("Pause this app under the same migration first."); }
    }

    private void CheckSibling(string key)
    {
        if (IApp.NameOf(BrainId(), key) is null || key == this.GetPrimaryKeyString())
        { throw new ArgumentException("A migration target must be another app in the same brain."); }
    }
}
