using System.Runtime.CompilerServices;
using DigitalBrain.Testing.Aspire;

[assembly: InternalsVisibleTo("DigitalBrain.Framework.Tests.E2E")]

namespace DigitalBrain.Testing.E2E;

public sealed record BrowserOptions
{
    public bool? Headless { get; init; }
    public BrowserLocation? Location { get; init; }
    public float? SlowMoMilliseconds { get; init; }
    // A shared host serves many facts, each opening its own context; an idle primary session
    // would keep syncing shell state against theirs. Turn it off to lease browsers per fact.
    public bool PrimarySession { get; init; } = true;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan AssertionTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

internal sealed record ResolvedBrowserOptions(bool Headless, float SlowMoMilliseconds,
    TimeSpan StartupTimeout, TimeSpan AssertionTimeout, bool PrimarySession, BrowserLocation Location = BrowserLocation.Local);

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
        var location = options.Location
            ?? (Enum.TryParse<BrowserLocation>(locationRequested, ignoreCase: true, out var requested) ? requested
                : !string.IsNullOrEmpty(locationRequested) ? throw new ArgumentException($"Unknown browser location '{locationRequested}'.", nameof(locationRequested))
                : headless && !debuggerAttached ? BrowserLocation.Container : BrowserLocation.Local);
        if (!headless && location == BrowserLocation.Container)
        { throw new InvalidOperationException("A headed browser runs locally: a container browser cannot show a window."); }
        return new(headless, delay, options.StartupTimeout, options.AssertionTimeout, options.PrimarySession, location);
    }
}
