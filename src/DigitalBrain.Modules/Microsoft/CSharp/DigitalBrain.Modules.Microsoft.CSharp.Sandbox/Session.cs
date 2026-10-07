using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// One brain's connection and its loaded behaviors. The control loop follows the brain's stream: an
// assignment loads (or swaps) one behavior, a retirement unloads it, a logs request is answered from
// its ring buffer. Every load and every exit is reported, so the brain counts failures per behavior.
internal sealed class Session(SessionConnection connection, BehaviorLoader loader, ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BehaviorRun> _behaviors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);

    public IReadOnlyDictionary<string, BehaviorRun> Behaviors => _behaviors;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var (kind, data) in connection.Control.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                switch (kind)
                {
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

    private async Task RetireAsync(string behavior, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_behaviors.TryRemove(behavior, out var run)) { return; }
            await run.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Unloaded behavior {Behavior} {Hash}", run.Id, run.Hash);
            await ReportAsync(new(run.Id, run.Hash, "Stopped", 0, null), cancellationToken).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
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
