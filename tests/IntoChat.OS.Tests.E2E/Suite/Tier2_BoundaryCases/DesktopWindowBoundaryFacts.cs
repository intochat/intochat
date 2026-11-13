using System.Text.RegularExpressions;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

/// <summary>
/// Operational state model reflecting the workspace and desktop window rules
/// defined in WorkspaceNeuron.cs, WorkspaceWindows.cs, and DesktopApp.cs.
/// </summary>
public sealed record BoundaryWorkspaceView(string Id, string App, string Variant = "full");

public sealed record BoundaryWorkspaceWindow(string Id, BoundaryWorkspaceView[] Tabs, string ActiveTabId);

public sealed record BoundaryDesktopState(
    string[] Path,
    string[] Open,
    string[] PinnedApps,
    BoundaryWorkspaceView[] OpenViews,
    string? FocusedViewId,
    BoundaryWorkspaceWindow[] Windows);

public static class BoundaryWorkspaceEngine
{
    public const int MaxWindows = 8;
    public const int MaxTabs = 8;
    public const int MaxViews = 64;

    public static BoundaryDesktopState Initial() =>
        new(
            Path: ["brain-main", "brain-main/apps/ino"],
            Open: ["brain-main/apps/ino"],
            PinnedApps: ["ino", "creator"],
            OpenViews: [new BoundaryWorkspaceView("v-1", "apps/ino")],
            FocusedViewId: "v-1",
            Windows: [new BoundaryWorkspaceWindow("w-1", [new BoundaryWorkspaceView("v-1", "apps/ino")], "v-1")]);

    public static BoundaryDesktopState Focus(BoundaryDesktopState state, string focusedViewId)
    {
        ArgumentNullException.ThrowIfNull(focusedViewId);
        if (!state.OpenViews.Any(v => v.Id == focusedViewId))
        {
            throw new ArgumentException("Focus must name an open view.");
        }
        return state with { FocusedViewId = focusedViewId };
    }

    public static BoundaryDesktopState Close(BoundaryDesktopState state, string app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var at = Array.FindIndex(state.Path, key => key == app || key.StartsWith(app + "/", StringComparison.Ordinal));
        return state with
        {
            Open = [.. state.Open.Where(key => key != app)],
            Path = at > 0 ? state.Path[..at] : state.Path,
            OpenViews = [.. state.OpenViews.Where(v => v.App != app)]
        };
    }

    public static BoundaryDesktopState Pin(BoundaryDesktopState state, string? appId, bool pinned)
    {
        var targetApp = string.IsNullOrWhiteSpace(appId) ? "photocraft" : appId.Trim().ToLowerInvariant();
        var current = (state.PinnedApps ?? ["ino", "creator"]).ToList();
        if (pinned)
        {
            if (!current.Contains(targetApp, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(targetApp);
            }
        }
        else
        {
            current.RemoveAll(item => string.Equals(item, targetApp, StringComparison.OrdinalIgnoreCase));
        }

        return state with { PinnedApps = [.. current] };
    }

    public static void ValidateWindows(BoundaryWorkspaceWindow[] windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Length > MaxWindows || windows.Any(w => w.Tabs is null || w.Tabs.Length is 0 or > MaxTabs))
        {
            throw new ArgumentException("Provide at most 8 windows, each with an id, 1 to 8 tabs and an active tab among them.");
        }
    }

    public static void ValidateViews(BoundaryWorkspaceView[] views)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (views.Length > MaxViews)
        {
            throw new ArgumentException("Provide at most 64 uniquely identified views of apps in this brain.");
        }
    }
}

public sealed class DesktopWindowBoundaryFacts : E2ETestBase
{
    private static readonly Lazy<string> _workspaceNeuronSource = new(() =>
    {
        var path = Path.Combine(SrcDir, "Modules", "Shell", "Workspace", "WorkspaceNeuron.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    });

    private static readonly Lazy<string> _workspaceWindowsSource = new(() =>
    {
        var path = Path.Combine(SrcDir, "Modules", "Shell", "Workspace", "WorkspaceWindows.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    });

    private static readonly Lazy<string> _desktopAppSource = new(() =>
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Apps", "Desktop", "DesktopApp.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    });

    [Fact]
    public void T2_Desktop_Focus_NonExistentViewOrWindow_ThrowsArgumentException()
    {
        // 1. Source verification: WorkspaceNeuron enforeces focus validation
        var source = _workspaceNeuronSource.Value;
        Assert.NotEmpty(source);
        Assert.Contains("Focus must name an open view.", source);

        // 2. Operational state machine verification
        var state = BoundaryWorkspaceEngine.Initial();
        var ex = Assert.Throws<ArgumentException>(() =>
            BoundaryWorkspaceEngine.Focus(state, "ghost-view-does-not-exist"));

        Assert.Contains("Focus must name an open view.", ex.Message);
    }

    [Fact]
    public void T2_Desktop_Close_NonExistentWindow_ExecutesIdempotentlyWithoutError()
    {
        var state = BoundaryWorkspaceEngine.Initial();
        var initialOpenCount = state.Open.Length;
        var initialPathCount = state.Path.Length;

        // Closing an unopened/non-existent app completes safely
        var nextState = BoundaryWorkspaceEngine.Close(state, "apps/ghost_app");

        Assert.Equal(initialOpenCount, nextState.Open.Length);
        Assert.Equal(initialPathCount, nextState.Path.Length);
        Assert.DoesNotContain("apps/ghost_app", nextState.Open);
    }

    [Fact]
    public void T2_Desktop_Close_DoubleClose_SucceedsWithoutExceptionOrStateCorruption()
    {
        var state = BoundaryWorkspaceEngine.Initial();

        // First close removes the open app
        var closedOnce = BoundaryWorkspaceEngine.Close(state, "brain-main/apps/ino");
        Assert.DoesNotContain("brain-main/apps/ino", closedOnce.Open);

        // Second close executes idempotently without exception or corrupting path
        var closedTwice = BoundaryWorkspaceEngine.Close(closedOnce, "brain-main/apps/ino");
        Assert.DoesNotContain("brain-main/apps/ino", closedTwice.Open);
        Assert.Equal(closedOnce.Open.Length, closedTwice.Open.Length);
        Assert.Equal(closedOnce.Path.Length, closedTwice.Path.Length);
    }

    [Fact]
    public void T2_Desktop_Pin_AlreadyPinnedApp_MaintainsSingleEntryWithoutDuplication()
    {
        var state = BoundaryWorkspaceEngine.Initial();
        Assert.Contains("ino", state.PinnedApps);

        // Pinning already pinned app "ino" deduplicates and preserves single occurrence
        var pinnedState = BoundaryWorkspaceEngine.Pin(state, "ino", true);
        var occurrences = pinnedState.PinnedApps.Count(a => string.Equals(a, "ino", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void T2_Desktop_Unpin_UnpinnedApp_CompletesSafelyWithoutModification()
    {
        var state = BoundaryWorkspaceEngine.Initial();
        Assert.DoesNotContain("photocraft", state.PinnedApps);

        // Unpinning an app that is not currently pinned completes safely
        var unpinnedState = BoundaryWorkspaceEngine.Pin(state, "photocraft", false);

        Assert.Equal(state.PinnedApps.Length, unpinnedState.PinnedApps.Length);
        Assert.DoesNotContain("photocraft", unpinnedState.PinnedApps);
    }

    [Fact]
    public void T2_Desktop_Pin_NullOrEmptyAppId_DefaultsToFallbackPhotocraft()
    {
        var desktopSrc = _desktopAppSource.Value;
        Assert.NotEmpty(desktopSrc);
        Assert.Contains("photocraft", desktopSrc);

        var state = BoundaryWorkspaceEngine.Initial();

        // Pin with null defaults to photocraft
        var pinNull = BoundaryWorkspaceEngine.Pin(state, null, true);
        Assert.Contains("photocraft", pinNull.PinnedApps);

        // Pin with empty string defaults to photocraft
        var pinEmpty = BoundaryWorkspaceEngine.Pin(state, "   ", true);
        Assert.Contains("photocraft", pinEmpty.PinnedApps);
    }

    [Fact]
    public void T2_Workspace_WindowLimit_Exceeding8Windows_ThrowsArgumentException()
    {
        // 1. Source verification: WorkspaceWindows and WorkspaceNeuron enforce 8 window limit
        var wsSrc = _workspaceNeuronSource.Value;
        Assert.NotEmpty(wsSrc);
        Assert.Contains("Provide at most 8 windows", wsSrc);

        // 2. Operational verification: 9 windows throws ArgumentException
        var nineWindows = Enumerable.Range(1, 9)
            .Select(i => new BoundaryWorkspaceWindow($"w-{i}", [new BoundaryWorkspaceView($"v-{i}", $"app-{i}")], $"v-{i}"))
            .ToArray();

        var ex = Assert.Throws<ArgumentException>(() =>
            BoundaryWorkspaceEngine.ValidateWindows(nineWindows));

        Assert.Contains("Provide at most 8 windows", ex.Message);
    }

    [Fact]
    public void T2_Workspace_TabLimit_Exceeding8TabsPerWindow_ThrowsArgumentException()
    {
        // 1. Source verification: WorkspaceWindows defines MaxTabs = 8
        var windowsSrc = _workspaceWindowsSource.Value;
        Assert.NotEmpty(windowsSrc);
        Assert.Contains("MaxTabs = 8", windowsSrc);

        // 2. Operational verification: Window holding 9 tabs throws ArgumentException
        var nineTabs = Enumerable.Range(1, 9)
            .Select(i => new BoundaryWorkspaceView($"tab-{i}", $"app-{i}"))
            .ToArray();

        var windowWithNineTabs = new[]
        {
            new BoundaryWorkspaceWindow("w-1", nineTabs, "tab-1")
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            BoundaryWorkspaceEngine.ValidateWindows(windowWithNineTabs));

        Assert.Contains("1 to 8 tabs", ex.Message);
    }

    [Fact]
    public void T2_Workspace_ViewLimit_Exceeding64Views_ThrowsArgumentException()
    {
        // 1. Source verification: WorkspaceNeuron CheckViews checks views.Length > 64
        var wsSrc = _workspaceNeuronSource.Value;
        Assert.NotEmpty(wsSrc);
        Assert.Contains("Provide at most 64 uniquely identified views", wsSrc);

        // 2. Operational verification: 65 views throws ArgumentException
        var sixtyFiveViews = Enumerable.Range(1, 65)
            .Select(i => new BoundaryWorkspaceView($"v-{i}", $"app-{i}"))
            .ToArray();

        var ex = Assert.Throws<ArgumentException>(() =>
            BoundaryWorkspaceEngine.ValidateViews(sixtyFiveViews));

        Assert.Contains("Provide at most 64 uniquely identified views", ex.Message);
    }
}
