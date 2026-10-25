using IntoChat.OS.Modules.Microsoft.Playwright;

namespace IntoChat.OS.Testing.Fakes;

// A page FakeBrowserSessionProvider hands out; its knobs script navigation and detachment.
public sealed class FakeBrowserPageSession(FakeBrowserSessionProvider provider) : IBrowserPageSession
{
    public bool FailDispose { get; set; }
    public bool FailNavigate { get; set; }
    public TaskCompletionSource? PendingDispose { get; set; }
    public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource NavigationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Navigations { get; } = [];
    public bool Disposed { get; private set; }
    public bool Connected => !Disposed;
    public string? Url { get; private set; } = provider.Url;
    public string? Title { get; } = provider.Title;

    public async Task<BrowserObservation> NavigateAsync(string url, CancellationToken ct)
    {
        Navigations.Add(url);
        NavigationStarted.TrySetResult();
        if (provider.HangNavigation) { await Task.Delay(Timeout.Infinite, ct); }
        if (FailNavigate) { throw new InvalidOperationException("Load rejected"); }
        Url = url;
        return await SnapshotAsync(ct);
    }

    public Task<BrowserObservation> SnapshotAsync(CancellationToken ct) => Task.FromResult(new BrowserObservation(Url!, Title!, "", []));
    public Task<BrowserObservation> ClickAsync(string selector, CancellationToken ct) => SnapshotAsync(ct);
    public Task<BrowserObservation> FillAsync(string selector, string value, CancellationToken ct) => SnapshotAsync(ct);

    public Task<string> EvaluateAsync(string expression, CancellationToken ct)
    {
        provider.Expressions.Add(expression);
        if (provider.EvaluateFailure is { } failure) { return Task.FromException<string>(new InvalidOperationException(failure)); }
        return Task.FromResult(provider.Evaluate?.Invoke(expression) ?? "null");
    }

    public async ValueTask DisposeAsync()
    {
        DisposeStarted.TrySetResult();
        if (FailDispose) { throw new InvalidOperationException("Detachment failed."); }
        if (PendingDispose is { } pending) { await pending.Task; }
        Disposed = true;
    }
}
