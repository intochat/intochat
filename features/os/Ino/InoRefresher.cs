// Coalesces "the view is dirty" into publishes from one writer: a refresh requested while a publish runs
// causes one more publish afterwards, which reads state after the previous one finished, so the last
// published tree reflects every mutation committed before the last request.
public sealed class InoRefresher(Func<Task> publish)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requested;

    public async Task Refresh()
    {
        Interlocked.Increment(ref _requested);
        await _gate.WaitAsync();
        try
        {
            while (Interlocked.Exchange(ref _requested, 0) > 0) { await publish(); }
        }
        finally { _gate.Release(); }
    }
}
