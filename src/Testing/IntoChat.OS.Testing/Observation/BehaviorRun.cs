using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Testing;

public sealed class BehaviorRun : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly ObservedBrain _brain;
    private int _disposed;
    private readonly TestExecutionOptions _execution;
    internal BehaviorRun(IIntoChatOS brain, Func<IIntoChatOS, CancellationToken, Task> body, CancellationToken ct)
    {
        _execution = (brain as ITrackedBrain)?.Execution ?? new();
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _brain = new(brain);
        Completion = Task.Run(() => body(_brain, _cancel.Token), CancellationToken.None);
    }
    public Task Completion { get; }
    public async Task WaitForSubscriptionAsync<T>(INeuron source, CancellationToken ct = default) where T : Signal
    {
        var ready = _brain.Ready(source.GetGrainId(), typeof(T)).Task;
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
    private sealed class ObservedBrain(IIntoChatOS inner) : IIntoChatOS, IBrainSubscriptions
    {
        private readonly IBrainSubscriptions _subscriptions = inner as IBrainSubscriptions
            ?? throw new ArgumentException("RunBehavior needs a brain that hands out subscription handles (the test harness brains do).", nameof(inner));
        private readonly ConcurrentDictionary<(GrainId, Type), TaskCompletionSource> _ready = new();
        internal TaskCompletionSource Ready(GrainId source, Type type)
            => _ready.GetOrAdd((source, type), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public T Get<T>(NeuronId id) where T : INeuron => inner.Get<T>(id);
        public T Neuron<T>(NeuronId relative) where T : INeuron => inner.Neuron<T>(relative);
        public IBrain Self => inner.Self;
        public IKernelView Kernel => inner.Kernel;
        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => On<T>(source, static () => Task.CompletedTask, cancellationToken);
        public async IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : Signal
        {
            await using var subscription = await SubscribeAsync<T>(source, cancellationToken).ConfigureAwait(false);
            await onSubscribed().ConfigureAwait(false);
            await foreach (var signal in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false)) { yield return signal; }
        }
        public async Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        {
            var subscription = await _subscriptions.SubscribeAsync<T>(source, cancellationToken).ConfigureAwait(false);
            Ready(source.GetGrainId(), typeof(T)).TrySetResult();
            return subscription;
        }
    }
}
