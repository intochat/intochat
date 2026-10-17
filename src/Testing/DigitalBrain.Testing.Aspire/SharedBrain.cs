namespace DigitalBrain.Testing.Aspire;

// One booted host for every fact of a collection. Boots on first lease; a failed boot is remembered
// and rethrown to every dependent fact. Facts that change deployment-global state must not lease it.
public abstract class SharedBrain<TBrain> : IAsyncDisposable where TBrain : class, IAsyncDisposable
{
    private readonly Lazy<Task<TBrain>> _host;

    protected SharedBrain() => _host = new(() => Task.Run(() => StartHostAsync(CancellationToken.None)));

    protected abstract Task<TBrain> StartHostAsync(CancellationToken cancellationToken);

    // A fact's view of the host it does not own: disposing it releases only what the fact observed.
    protected abstract TBrain Lease(TBrain host);

    // Boots the host eagerly, outside any fact's own timeout — a test framework's fixture
    // initialization is the natural place to pay a boot that can take several minutes on CI.
    public async ValueTask WarmUpAsync() => await _host.Value.ConfigureAwait(false);

    public async Task<TBrain> LeaseAsync(CancellationToken cancellationToken = default)
    {
        var host = await _host.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Lease(host);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host.IsValueCreated)
            {
                try { await (await _host.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch when (_host.Value.IsFaulted) { /* boot failure already surfaced to the facts */ }
            }
        }
        finally { await OnDisposedAsync().ConfigureAwait(false); }
    }

    protected virtual ValueTask OnDisposedAsync() => ValueTask.CompletedTask;
}
