using Microsoft.Extensions.Logging;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// A lease held on a source: disposing it stops the renewal and, unless the silo is shutting down, withdraws the watch.
public interface IWatchRenewal : IAsyncDisposable;

internal sealed class WatchRenewal(Func<Task>? unwatch, Action<WatchRenewal> released, ILogger logger) : IWatchRenewal
{
    private IDisposable? _timer;
    private int _released;

    internal bool Released => Volatile.Read(ref _released) == 1;

    internal void Run(IDisposable timer) => _timer = timer;

    public ValueTask DisposeAsync() => Release(withdraw: true);

    internal async ValueTask Release(bool withdraw)
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) { return; }
        _timer?.Dispose();
        released(this);
        if (!withdraw || unwatch is null) { return; }
        try { await unwatch(); }
        catch (OperationCanceledException error) { logger.LogDebug(error, "A watch was withdrawn while its activation was ending."); }
        catch (Orleans.Runtime.SiloUnavailableException error) { logger.LogDebug(error, "A watch was not withdrawn: the silo is unavailable."); }
    }
}
