namespace DigitalBrain.Platform.Compute;

// One grain's state writes, coalesced: every change made while a write is out rides the one write queued behind it,
// so however many changes arrive during a write they cost one more write, and a burst costs at most two. Every caller
// awaits a write that started after its change, so a change is durable when its call returns. The grain is reentrant
// and applies its change to the state before calling Save, with no await in between: the activation runs one turn at
// a time.
internal sealed class CoalescedWrites(Func<Task> write)
{
    private Task _out = Task.CompletedTask;
    private Task? _queued;

    public Task Save()
    {
        if (_queued is not null) { return _queued; }
        if (_out.IsCompleted) { return _out = write(); }
        return _queued = AfterOut();
    }

    private async Task AfterOut()
    {
        // A failed write fails only its own callers; the queued write carries the state as it is now. The
        // continuation stays on the activation's scheduler.
        await _out.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        _queued = null;
        await (_out = write());
    }
}
