using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// One brain's connection and its loaded behaviors. The control loop follows the brain's stream: ready
// names the desired set, an assignment loads (or swaps) one behavior, a retirement unloads it, a logs request is answered from
// its ring buffer. Every load and every exit is reported, so the brain counts failures per behavior.
internal sealed class Session(SessionConnection connection, BehaviorLoader loader, ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BehaviorRun> _behaviors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);

    public IReadOnlyDictionary<string, BehaviorRun> Behaviors => _behaviors;

    internal void RefreshToken(string token) => connection.TryUpdateToken(token);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var (kind, data) in connection.Control.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                switch (kind)
                {
                    case ReadyEvent.Kind: await KeepOnlyAsync(JsonSerializer.Deserialize<ReadyEvent>(data, RuntimeProtocol.Json)!.Behaviors, cancellationToken).ConfigureAwait(false); break;
                    case AssignmentEvent.Kind: await AssignAsync(JsonSerializer.Deserialize<AssignmentEvent>(data, RuntimeProtocol.Json)!, cancellationToken).ConfigureAwait(false); break;
                    case RetireEvent.Kind: await RetireAsync(JsonSerializer.Deserialize<RetireEvent>(data, RuntimeProtocol.Json)!.Behavior, cancellationToken).ConfigureAwait(false); break;
                    case LogsRequestEvent.Kind: await AnswerLogsAsync(JsonSerializer.Deserialize<LogsRequestEvent>(data, RuntimeProtocol.Json)!, cancellationToken).ConfigureAwait(false); break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Session event {Kind} failed", kind);
            }
        }
    }

    private async Task AssignAsync(AssignmentEvent assignment, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A reconnecting stream resends every assignment: one already running as assigned keeps running.
            if (_behaviors.TryGetValue(assignment.Behavior, out var current) && Same(current.Assignment, assignment)) { return; }
            // A swap unloads only this behavior; the others keep running.
            if (_behaviors.TryRemove(assignment.Behavior, out var previous)) { await previous.DisposeAsync().ConfigureAwait(false); }
            BehaviorRun run;
            try { run = await loader.LoadAsync(connection, assignment, logger, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await ReportAsync(new(assignment.Behavior, assignment.Hash, "Exited", 1, error.Message), cancellationToken).ConfigureAwait(false);
                return;
            }
            _behaviors[assignment.Behavior] = run;
            logger.LogInformation("Loaded behavior {Behavior} {Hash}", assignment.Behavior, assignment.Hash);
            await ReportAsync(new(run.Id, run.Hash, "Running", null, null), cancellationToken).ConfigureAwait(false);
            _ = ReportExitAsync(run);
        }
        finally { _changes.Release(); }
    }

    private static bool Same(AssignmentEvent running, AssignmentEvent assigned)
        => running.Hash == assigned.Hash && running.Settings.Count == assigned.Settings.Count
            && running.Settings.All(setting => assigned.Settings.TryGetValue(setting.Key, out var value) && value == setting.Value);

    private async Task RetireAsync(string behavior, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await UnloadAsync(behavior, cancellationToken).ConfigureAwait(false); }
        finally { _changes.Release(); }
    }

    // The brain's whole desired set, sent on every (re)connect: a retirement pushed while the stream was down
    // never arrived, so whatever this session holds beyond the set is unloaded now.
    private async Task KeepOnlyAsync(string[] wanted, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var behavior in _behaviors.Keys.Except(wanted, StringComparer.Ordinal).ToArray())
            { await UnloadAsync(behavior, cancellationToken).ConfigureAwait(false); }
        }
        finally { _changes.Release(); }
    }

    private async Task UnloadAsync(string behavior, CancellationToken cancellationToken)
    {
        if (!_behaviors.TryRemove(behavior, out var run)) { return; }
        await run.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("Unloaded behavior {Behavior} {Hash}", run.Id, run.Hash);
        await ReportAsync(new(run.Id, run.Hash, "Stopped", 0, null), cancellationToken).ConfigureAwait(false);
    }

    // An exit the brain did not ask for: reported once, unless the run was retired or swapped first.
    private async Task ReportExitAsync(BehaviorRun run)
    {
        var exit = await run.Completion.ConfigureAwait(false);
        if (!_behaviors.TryGetValue(run.Id, out var current) || current != run) { return; }
        _behaviors.TryRemove(new(run.Id, run));
        await ReportAsync(new(run.Id, run.Hash, "Exited", exit, run.Error), connection.Stopping).ConfigureAwait(false);
        await run.DisposeAsync().ConfigureAwait(false);
    }

    private Task AnswerLogsAsync(LogsRequestEvent request, CancellationToken cancellationToken)
        => connection.ReportLogsAsync(request.Request, _behaviors.TryGetValue(request.Behavior, out var run) ? run.Tail(request.Tail) : "", cancellationToken);

    private async Task ReportAsync(BehaviorStatusReport status, CancellationToken cancellationToken)
    {
        try { await connection.ReportStatusAsync(status, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
        { logger.LogWarning(error, "Could not report {Behavior} {Status}", status.Behavior, status.Status); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var run in _behaviors.Values) { await run.DisposeAsync().ConfigureAwait(false); }
        _behaviors.Clear();
        await connection.DisposeAsync().ConfigureAwait(false);
        _changes.Dispose();
    }
}
