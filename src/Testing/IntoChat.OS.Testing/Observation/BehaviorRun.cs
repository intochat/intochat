using System.Collections.Concurrent;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

public sealed class BehaviorRun : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly ObservedBrain _brain;
    private int _disposed;
    private readonly TestExecutionOptions _execution;
    internal BehaviorRun(ITestBrain brain, Func<ISignalWatch, CancellationToken, Task> body, CancellationToken ct)
    {
        _execution = (brain as ITrackedBrain)?.Execution ?? new();
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _brain = new(brain);
        Completion = Task.Run(() => body(_brain, _cancel.Token), CancellationToken.None);
    }
    public Task Completion { get; }
    // source is the reference brain.Get returned.
    public async Task WaitForSubscriptionAsync<T>(object source, CancellationToken ct = default) where T : DigitalBrain.Signal
    {
        var ready = _brain.Ready(ObservedBrain.Key(source), typeof(T)).Task;
        await Task.WhenAny(ready, Completion).WaitAsync(_execution.AssertionTimeout, ct).ConfigureAwait(false);
        if (!ready.IsCompletedSuccessfully)
        {
            await Completion.ConfigureAwait(false);
            throw new InvalidOperationException("Behavior ended before establishing the requested subscription.");
        }
        await ready.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        var canceling = _cancel.CancelAsync();
        var shutdown = Task.WhenAll(canceling, Completion);
        try { await shutdown.WaitAsync(_execution.CleanupTimeout).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested && canceling.IsCompletedSuccessfully) { }
        finally
        {
            // A noncooperative task or callback can outlive the deadline. Observe its
            // eventual fault and release the token source once callbacks have finished.
            _ = shutdown.ContinueWith(task => { _ = task.Exception; _cancel.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private sealed class ObservedBrain(ISignalWatch inner) : ISignalWatch
    {
        private readonly ConcurrentDictionary<(string, Type), TaskCompletionSource> _ready = new();
        internal static string Key(object source) => source is DigitalBrain.Invocation.NeuronProxy proxy
            ? proxy.Target.ToString()
            : throw new ArgumentException("Wait for a neuron obtained from the brain's Get.", nameof(source));
        internal TaskCompletionSource Ready(string source, Type type)
            => _ready.GetOrAdd((source, type), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public IAsyncEnumerable<T> On<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
            => inner.On<T>(source, async () =>
            {
                await onSubscribed().ConfigureAwait(false);
                Ready(Key(source), typeof(T)).TrySetResult();
            }, cancellationToken);
    }
}
