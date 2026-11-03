using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task Pause(string migration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migration);
        await Check("Pause", new MigrationIsSingle(Snapshot.Migration is { } existing && existing != migration));
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
        { await runtime.Retire(Key, CancellationToken.None); }
    }

    public async Task<AppState> Archive(string migration)
    {
        await Check("Archive", new MigrationIsPausedFirst(Snapshot.Migration == migration && Snapshot.MigrationArchive is not null));
        return Snapshot.MigrationArchive!.Clone();
    }

    public async Task Adopt(string migration, string source)
    {
        await Check("Adopt",
            new MigrationIsPausedFirst(Snapshot.Migration == migration && Snapshot.MigrationArchive is not null),
            new MigrationTargetIsSibling(source == Key ? "" : source, BrainId()),
            new MigrationAdoptsOneSource(Snapshot.MigrationSource, source));
        if (Snapshot.MigrationSource is not null)
        {
            return;
        }
        var original = await Get<IAppMigration>(AppId(source)).Archive(migration);
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
        await Check("Redirect",
            new MigrationIsPausedFirst(Snapshot.Migration == migration && Snapshot.MigrationArchive is not null),
            new MigrationTargetIsSibling(target == Key ? "" : target, BrainId()),
            new MigrationRedirectsOnce(Snapshot.MigrationTarget is { } existing && existing != target));
        var next = Snapshot.Clone();
        next.MigrationTarget = target;
        await Persist(next);
        // Include requests accepted while the source was paused. New requests now go directly
        // to the target; replay of this transfer is deduplicated by original request identity.
        await Get<IAppMigration>(AppId(target)).MergeRequests(Snapshot.RequestRetention);
    }

    public async Task Resume(string migration)
    {
        await Check("Resume",
            new MigrationIsPausedFirst(Snapshot.Migration == migration && Snapshot.MigrationArchive is not null),
            new AliasDoesNotRun(Snapshot.MigrationTarget is not null));
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

    public async Task MergeRequests(RetainedRequest[] requests)
    {
        await Check("MergeRequests", new MigrationIsRecorded(Snapshot.Migration is not null));
        foreach (var request in requests.Select(item => item.Request))
        {
            await Accept(request.Operation, request.Input, request.RequestId);
            if (Snapshot.MigrationRequests.Any(held => held.RequestId == request.RequestId)) { continue; }
            var next = Snapshot.Clone();
            next.MigrationRequests = [.. next.MigrationRequests, request];
            await Persist(next);
        }
    }

    private static void AddRequests(AppState next, RetainedRequest[] requests)
    {
        foreach (var request in requests.Select(item => item.Request))
        {
            if (next.RequestRetention.Any(item => item.Request.RequestId == request.RequestId)) { continue; }
            next.RequestSequence++;
            next.RequestRetention = [.. next.RequestRetention, new(next.RequestSequence, request)];
            next.MigrationRequests = [.. next.MigrationRequests, request];
        }
    }

    public async Task Accept(string operation, string input, Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(input);
        var prior = Snapshot.RequestRetention.Select(item => item.Request)
            .Concat(Snapshot.MigrationRequests).FirstOrDefault(item => item.RequestId == requestId);
        await Check("Accept",
            new RequestPayloadFits(input.Length),
            new RequestIdIsPresent(requestId),
            new RequestIdIsFresh(prior is not null && (prior.Operation != operation || prior.Input != input)));
        if (prior is not null)
        {
            return;
        }
        var request = new AppRequested(operation, input, requestId);
        if (!Snapshot.MigrationPaused)
        {
            string[] operations = [.. (Snapshot.Manifest?.Operations ?? []).Select(candidate => candidate.Name)];
            await Check("Accept", new OperationIsDeclared(operation, operations, Name()));
            if (await Rejection(request) is { } rejected)
            { throw new RuleViolationException(new Rejected(operation, [rejected.Rule], [rejected.Reason])); }
        }
        var next = Snapshot.Clone();
        next.RequestSequence++;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(2 * RetainedCapacity - 1), new(next.RequestSequence, request)];
        await Save(next, new AppRequested(operation, input, requestId));
    }
}
