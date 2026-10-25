using System.Diagnostics;

namespace IntoChat.OS.Testing.Aspire;

// Where Chromium runs. A container needs only Docker; a local browser needs `playwright install`
// and is what a person debugging wants, because only it can show a window.
public enum BrowserLocation { Container, Local }

// One rule for the AppHost (which adds the container) and the browser (which connects to it):
// INTOCHATOS_E2E_BROWSER picks explicitly; otherwise a headless run without a debugger uses the container.
/// <summary>Where Chromium runs, read from INTOCHATOS_E2E_BROWSER and INTOCHATOS_E2E_HEADED.</summary>
public static class BrowserEnvironment
{
    /// <summary>True when INTOCHATOS_E2E_HEADED is 1.</summary>
    public static bool HeadedRequested => Environment.GetEnvironmentVariable("INTOCHATOS_E2E_HEADED") == "1";
    /// <summary>The raw INTOCHATOS_E2E_BROWSER value, if any.</summary>
    public static string? LocationRequested => Environment.GetEnvironmentVariable("INTOCHATOS_E2E_BROWSER");

    internal static BrowserLocation Location
        => Resolve(headless: !(HeadedRequested || Debugger.IsAttached), Debugger.IsAttached, LocationRequested);

    /// <summary>Picks the browser location from the explicit request, else container when headless without a debugger.</summary>
    public static BrowserLocation Resolve(bool headless, bool debuggerAttached, string? locationRequested)
        => Enum.TryParse<BrowserLocation>(locationRequested, ignoreCase: true, out var requested) ? requested
            : !string.IsNullOrEmpty(locationRequested) ? throw new ArgumentException($"Unknown browser location '{locationRequested}'.", nameof(locationRequested))
            : headless && !debuggerAttached ? BrowserLocation.Container : BrowserLocation.Local;
}
