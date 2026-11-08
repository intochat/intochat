using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Rules;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class AppNeuron
{
    public async Task Pause(string migration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migration);
        await Check("Pause", new MigrationIsSingle(State.Migration is { } existing && existing != migration));
        if (State.Migration is null)
        {
            var next = State.Clone();
            next.MigrationArchive = State.Clone();
            next.Migration = migration;
            next.MigrationPaused = true;
            next.Synapses = [.. next.Synapses.Select(entry => entry.Status == SynapseStatus.Requested
                ? entry with { Status = SynapseStatus.Unsettled, Errors = ["App migration retired the old program; the staged source is retained for review."] }
                : entry)];
            await Persist(next);
        }
        else if (State.Settings.GetValueOrDefault("CompletedAppMigration") != migration && !State.MigrationPaused)
        {
            var next = State.Clone();
            next.MigrationPaused = true;
            await Persist(next);
        }
        // Retrying retirement is necessary if its acknowledgement was lost. The persisted gate
        // also prevents activation from assigning this old program again.
        if (State.MigrationPaused)
        { await Runtime.Retire(Key, CancellationToken.None); }
    }

    public async Task<AppState> Archive(string migration)
    {
        await Check("Archive", new MigrationIsPausedFirst(State.Migration == migration && State.MigrationArchive is not null));
        return State.MigrationArchive!.Clone();
    }

    public async Task Adopt(string migration, string source)
    {
        await Check("Adopt",
            new MigrationIsPausedFirst(State.Migration == migration && State.MigrationArchive is not null),
            new MigrationTargetIsSibling(source == Key ? "" : source, BrainId()),
            new MigrationAdoptsOneSource(State.MigrationSource, source));
        if (State.MigrationSource is not null)
        {
            return;
        }
        var original = await Get<IAppMigration>(AppId(source)).Archive(migration);
        var next = State.Clone();
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
            new MigrationIsPausedFirst(State.Migration == migration && State.MigrationArchive is not null),
            new MigrationTargetIsSibling(target == Key ? "" : target, BrainId()),
            new MigrationRedirectsOnce(State.MigrationTarget is { } existing && existing != target));
        var next = State.Clone();
        next.MigrationTarget = target;
        await Persist(next);
        // Include requests accepted while the source was paused. New requests now go directly
        // to the target; replay of this transfer is deduplicated by original request identity.
        await Get<IAppMigration>(AppId(target)).MergeRequests(State.RequestRetention);
    }

    public async Task Resume(string migration)
    {
        await Check("Resume",
            new MigrationIsPausedFirst(State.Migration == migration && State.MigrationArchive is not null),
            new AliasDoesNotRun(State.MigrationTarget is not null));
        var next = State.Clone();
        next.MigrationPaused = false;
        next.Status = AppStatus.Deploying;
        await Persist(next);
        try { await Deploy(); }
        catch
        {
            var failed = State.Clone();
            failed.Status = AppStatus.Failed;
            await Persist(failed);
            throw;
        }
        next = State.Clone();
        next.Status = AppStatus.Live;
        next.PutSetting("CompletedAppMigration", migration);
        await Persist(next);
    }

    public async Task MergeRequests(RetainedRequest[] requests)
    {
        await Check("MergeRequests", new MigrationIsRecorded(State.Migration is not null));
        foreach (var request in requests.Select(item => item.Request))
        {
            await Accept(request.Operation, request.Input, request.RequestId);
            if (State.MigrationRequests.Any(held => held.RequestId == request.RequestId)) { continue; }
            var next = State.Clone();
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
        var prior = State.RequestRetention.Select(item => item.Request)
            .Concat(State.MigrationRequests).FirstOrDefault(item => item.RequestId == requestId);
        await Check("Accept",
            new RequestPayloadFits(input.Length),
            new RequestIdIsPresent(requestId),
            new RequestIdIsFresh(prior is not null && (prior.Operation != operation || prior.Input != input)));
        if (prior is not null)
        {
            return;
        }
        var request = new AppRequested(operation, input, requestId);
        if (!State.MigrationPaused)
        {
            string[] operations = [.. (State.Manifest?.Operations ?? []).Select(candidate => candidate.Name)];
            await Check("Accept", new OperationIsDeclared(operation, operations, Name()));
        }
        var next = State.Clone();
        next.RequestSequence++;
        next.RequestRetention = [.. next.RequestRetention.TakeLast(2 * RetainedCapacity - 1), new(next.RequestSequence, request)];
        await Save(next, new AppRequested(operation, input, requestId));
    }
}
