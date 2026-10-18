using DigitalBrain.Microsoft.Playwright;

namespace DigitalBrain.Testing.Fakes;

// Stands in for a CDP-attached browser: facts script attach timing and failures and the pages it hands out.
public sealed class FakeBrowserSessionProvider : IBrowserSessionProvider
{
    // Attaching throws "Browser unavailable".
    public bool Fail { get; set; }
    // Attaching answers this task instead of a fresh page.
    public TaskCompletionSource<IBrowserPageSession>? Pending { get; init; }
    // Attaching waits for this before answering.
    public TaskCompletionSource? Release { get; init; }
    // Attaching this session never answers (until cancelled).
    public string? BlockedSessionId { get; init; }
    // Urls the deployment serves itself (IBrowserSessionProvider.IsAppPage).
    public string? AppOrigin { get; init; }

    // What new pages report and how they behave.
    public string Url { get; init; } = "https://example.com";
    public string Title { get; init; } = "Example";
    // Navigation never completes (until cancelled), so facts can observe an in-flight activity.
    public bool HangNavigation { get; init; }
    // EvaluateAsync answers this for every expression; null answers "null".
    public Func<string, string>? Evaluate { get; init; }
    // EvaluateAsync throws InvalidOperationException with this message.
    public string? EvaluateFailure { get; init; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<BrowserAttachment> Attachments { get; } = [];
    public List<FakeBrowserPageSession> Pages { get; } = [];
    public List<string> Expressions { get; } = [];

    public bool IsAppPage(string url) => AppOrigin is { } origin && url.StartsWith(origin, StringComparison.Ordinal);

    public Task<IBrowserPageSession> StartAsync(string accountId, string sessionId, CancellationToken ct) => AttachAsync(new(1234, sessionId), ct);

    public async Task<IBrowserPageSession> AttachAsync(BrowserAttachment attachment, CancellationToken ct)
    {
        Attachments.Add(attachment);
        Started.TrySetResult();
        if (Fail) { throw new InvalidOperationException("Browser unavailable"); }
        if (Pending is not null) { return await Pending.Task; }
        if (Release is { } release) { await release.Task.WaitAsync(ct); }
        if (attachment.SessionId == BlockedSessionId) { await Task.Delay(Timeout.Infinite, ct); }
        var page = new FakeBrowserPageSession(this);
        Pages.Add(page);
        return page;
    }
}
