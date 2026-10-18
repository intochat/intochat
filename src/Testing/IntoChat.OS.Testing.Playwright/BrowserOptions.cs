using IntoChat.OS.Testing.Aspire;

namespace IntoChat.OS.Testing.Playwright;

public sealed record BrowserOptions
{
    public bool? Headless { get; init; }
    public BrowserLocation? Location { get; init; }
    public float? SlowMoMilliseconds { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan AssertionTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

internal sealed record ResolvedBrowserOptions(bool Headless, float SlowMoMilliseconds,
    TimeSpan StartupTimeout, TimeSpan AssertionTimeout, BrowserLocation Location = BrowserLocation.Local);

internal static class BrowserOptionsResolver
{
    internal static ResolvedBrowserOptions Resolve(BrowserOptions options, bool headedRequested, bool debuggerAttached,
        string? locationRequested = null)
    {
        TestExecutionOptions.ValidateTimeout(options.StartupTimeout);
        TestExecutionOptions.ValidateTimeout(options.AssertionTimeout);
        var headless = options.Headless ?? !(headedRequested || debuggerAttached);
        var delay = options.SlowMoMilliseconds ?? (headless ? 0 : 250);
        if (!float.IsFinite(delay) || delay < 0) { throw new ArgumentOutOfRangeException(nameof(options)); }
        var location = options.Location ?? BrowserEnvironment.Resolve(headless, debuggerAttached, locationRequested);
        if (!headless && location == BrowserLocation.Container)
        { throw new InvalidOperationException("A headed browser runs locally: a container browser cannot show a window."); }
        return new(headless, delay, options.StartupTimeout, options.AssertionTimeout, location);
    }
}
