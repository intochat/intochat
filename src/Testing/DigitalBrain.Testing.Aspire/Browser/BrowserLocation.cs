using System.Diagnostics;

namespace DigitalBrain.Testing.Aspire;

// Where Chromium runs. A container needs only Docker; a local browser needs `playwright install`
// and is what a person debugging wants, because only it can show a window.
public enum BrowserLocation { Container, Local }

// One rule for the AppHost (which adds the container) and the browser (which connects to it):
// DIGITALBRAIN_E2E_BROWSER picks explicitly; otherwise a headless run without a debugger uses the container.
internal static class BrowserEnvironment
{
    internal static bool HeadedRequested => Environment.GetEnvironmentVariable("DIGITALBRAIN_E2E_HEADED") == "1";
    internal static string? LocationRequested => Environment.GetEnvironmentVariable("DIGITALBRAIN_E2E_BROWSER");

    internal static BrowserLocation Location
        => Resolve(headless: !(HeadedRequested || Debugger.IsAttached), Debugger.IsAttached, LocationRequested);

    internal static BrowserLocation Resolve(bool headless, bool debuggerAttached, string? locationRequested)
        => Enum.TryParse<BrowserLocation>(locationRequested, ignoreCase: true, out var requested) ? requested
            : !string.IsNullOrEmpty(locationRequested) ? throw new ArgumentException($"Unknown browser location '{locationRequested}'.", nameof(locationRequested))
            : headless && !debuggerAttached ? BrowserLocation.Container : BrowserLocation.Local;
}
