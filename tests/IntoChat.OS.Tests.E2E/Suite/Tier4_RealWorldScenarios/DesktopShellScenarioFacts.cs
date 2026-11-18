using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier4_RealWorldScenarios;

public sealed record DesktopWindowItem(
    string WindowId,
    string AppId,
    string Title,
    bool IsActive,
    bool IsMinimized,
    bool IsPinned,
    int TabCount = 1);

public sealed record DesktopShellState
{
    public string LayoutMode { get; init; } = "smart-desktop";
    public string? ActiveWindowId { get; init; }
    public DesktopWindowItem[] Windows { get; init; } = [];
    public string[] PinnedApps { get; init; } = ["ino", "creator"];
    public string LastVerb { get; init; } = "start";
    public string? LastCaller { get; init; }
    public int TurnCount { get; init; }
}

[Signal("desktop-shell.launch")]
public sealed record DesktopLaunchSignal(string AppId, string Title, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("desktop-shell.focus")]
public sealed record DesktopFocusSignal(string WindowId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("desktop-shell.pin")]
public sealed record DesktopPinSignal(string WindowId, bool Pinned, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("desktop-shell.minimize")]
public sealed record DesktopMinimizeSignal(string WindowId, bool Minimized, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("desktop-shell.close")]
public sealed record DesktopCloseSignal(string WindowId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

[Signal("desktop-shell.add-tab")]
public sealed record DesktopAddTabSignal(string WindowId, string AppId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null) : Signal;

/*
# DesktopShell
Desktop shell screen managing window states, dock shortcuts, and active views.
*/
[Neuron("app.desktop_shell")]
public sealed class DesktopShellScreen : Screen<DesktopShellState>
{
    public const int MaxWindows = 8;
    public const int MaxTabs = 8;

    protected override View Render(DesktopShellState state)
    {
        var elements = new List<Node>
        {
            new Text($"Layout: {state.LayoutMode}"),
            new Text($"ActiveWindow: {state.ActiveWindowId ?? "none"}"),
            new Text($"WindowCount: {state.Windows.Length}"),
            new Text($"PinnedApps: {string.Join(',', state.PinnedApps)}"),
            new Text($"LastVerb: {state.LastVerb}"),
            new Text($"LastCaller: {state.LastCaller ?? "none"}"),
            new Text($"TurnCount: {state.TurnCount}"),
            new Button("launch-calculator", "Launch Calculator", Emits(new DesktopLaunchSignal("calculator", "Calculator"))),
            new Button("launch-notes", "Launch Notes", Emits(new DesktopLaunchSignal("notes", "Notes"))),
            new Button("launch-files", "Launch Files", Emits(new DesktopLaunchSignal("files", "Files")))
        };

        foreach (var w in state.Windows)
        {
            elements.Add(new Button($"focus-{w.WindowId}", $"Focus {w.Title}", Emits(new DesktopFocusSignal(w.WindowId))));
            elements.Add(new Button($"pin-{w.WindowId}", w.IsPinned ? "Unpin" : "Pin", Emits(new DesktopPinSignal(w.WindowId, !w.IsPinned))));
            elements.Add(new Button($"minimize-{w.WindowId}", w.IsMinimized ? "Restore" : "Minimize", Emits(new DesktopMinimizeSignal(w.WindowId, !w.IsMinimized))));
            elements.Add(new Button($"close-{w.WindowId}", $"Close {w.Title}", Emits(new DesktopCloseSignal(w.WindowId))));
            elements.Add(new Button($"tab-{w.WindowId}", "Add Tab", Emits(new DesktopAddTabSignal(w.WindowId, "sub-view"))));
        }

        return new View("DesktopShell", new Column([.. elements]));
    }

    [On]
    public Task OnLaunch(DesktopLaunchSignal signal)
    {
        if (State.Windows.Length >= MaxWindows)
        {
            throw new InvalidOperationException("Maximum window cap (8) reached.");
        }

        var windowId = $"w-{State.Windows.Length + 1}";
        var newWindow = new DesktopWindowItem(windowId, signal.AppId, signal.Title, IsActive: true, IsMinimized: false, IsPinned: false);
        var updatedWindows = State.Windows.Select(w => w with { IsActive = false }).Append(newWindow).ToArray();

        var next = State with
        {
            Windows = updatedWindows,
            ActiveWindowId = windowId,
            LastVerb = "launch",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }

    [On]
    public Task OnFocus(DesktopFocusSignal signal)
    {
        if (!State.Windows.Any(w => w.WindowId == signal.WindowId))
        {
            throw new ArgumentException("Focus must name an open view.");
        }

        var updatedWindows = State.Windows.Select(w => w with { IsActive = w.WindowId == signal.WindowId }).ToArray();
        var next = State with
        {
            Windows = updatedWindows,
            ActiveWindowId = signal.WindowId,
            LastVerb = "focus",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }

    [On]
    public Task OnPin(DesktopPinSignal signal)
    {
        var window = State.Windows.FirstOrDefault(w => w.WindowId == signal.WindowId)
            ?? throw new ArgumentException("Target window not found.");

        var updatedWindows = State.Windows.Select(w => w.WindowId == signal.WindowId ? w with { IsPinned = signal.Pinned } : w).ToArray();
        var pinned = State.PinnedApps.ToList();
        if (signal.Pinned)
        {
            if (!pinned.Contains(window.AppId, StringComparer.OrdinalIgnoreCase)) { pinned.Add(window.AppId); }
        }
        else
        {
            pinned.RemoveAll(app => string.Equals(app, window.AppId, StringComparison.OrdinalIgnoreCase));
        }

        var next = State with
        {
            Windows = updatedWindows,
            PinnedApps = [.. pinned],
            LastVerb = "pin",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }

    [On]
    public Task OnMinimize(DesktopMinimizeSignal signal)
    {
        _ = State.Windows.FirstOrDefault(w => w.WindowId == signal.WindowId)
            ?? throw new ArgumentException("Target window not found.");

        var updatedWindows = State.Windows.Select(w => w.WindowId == signal.WindowId ? w with { IsMinimized = signal.Minimized, IsActive = !signal.Minimized && w.IsActive } : w).ToArray();
        string? nextActive = State.ActiveWindowId;
        if (signal.Minimized && State.ActiveWindowId == signal.WindowId)
        {
            nextActive = updatedWindows.FirstOrDefault(w => !w.IsMinimized)?.WindowId;
        }

        var next = State with
        {
            Windows = updatedWindows,
            ActiveWindowId = nextActive,
            LastVerb = "minimize",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }

    [On]
    public Task OnClose(DesktopCloseSignal signal)
    {
        var remaining = State.Windows.Where(w => w.WindowId != signal.WindowId).ToArray();
        string? nextActive = State.ActiveWindowId == signal.WindowId ? remaining.LastOrDefault()?.WindowId : State.ActiveWindowId;

        var next = State with
        {
            Windows = remaining,
            ActiveWindowId = nextActive,
            LastVerb = "close",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }

    [On]
    public Task OnAddTab(DesktopAddTabSignal signal)
    {
        var window = State.Windows.FirstOrDefault(w => w.WindowId == signal.WindowId)
            ?? throw new ArgumentException("Target window not found.");

        if (window.TabCount >= MaxTabs)
        {
            throw new InvalidOperationException("Maximum tab cap (8) reached.");
        }

        var updatedWindows = State.Windows.Select(w => w.WindowId == signal.WindowId ? w with { TabCount = w.TabCount + 1 } : w).ToArray();
        var next = State with
        {
            Windows = updatedWindows,
            LastVerb = "add-tab",
            LastCaller = signal.By ?? Context.Caller?.ToString(),
            TurnCount = State.TurnCount + 1
        };
        return Show(next);
    }
}

public sealed class DesktopShellScenarioFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public DesktopShellScenarioFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T4_DesktopShell_InitialRead_RendersDefaultLayout_WithZeroWindowsAndInitialPinnedApps()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-init");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Equal("app.desktop_shell", view.Alias);
        Assert.Equal(1L, view.Generation);

        var raw = view.View.GetRawText();
        Assert.Contains("Layout: smart-desktop", raw);
        Assert.Contains("WindowCount: 0", raw);
        Assert.Contains("ActiveWindow: none", raw);
        Assert.Contains("PinnedApps: ino,creator", raw);
    }

    [Fact]
    public async Task T4_DesktopShell_LaunchApp_CreatesWindow_AdvancesGeneration_AndFocusesNewWindow()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-launch");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        _ = CreatePersonCaller(_fixture.Edge, "alice", "acct_1", brain);

        await screen.Activate("app.desktop_shell", 1L, "launch-calculator", fieldValue: null, by: "alice@intochat.io");
        await os.Idle();

        var updated = await screen.Read();
        Assert.NotNull(updated);
        Assert.Equal(2L, updated.Generation);

        var raw = updated.View.GetRawText();
        Assert.Contains("WindowCount: 1", raw);
        Assert.Contains("ActiveWindow: w-1", raw);
        Assert.Contains("LastVerb: launch", raw);
        Assert.Contains("LastCaller: alice@intochat.io", raw);
    }

    [Fact]
    public async Task T4_DesktopShell_MultiTurn_WindowVerbs_FocusPinMinimize_MaintainsMonotonicAudit()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-verbs");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        // Turn 1 -> 2: Launch calculator (w-1)
        await screen.Activate("app.desktop_shell", 1L, "launch-calculator", by: "alice");
        await os.Idle();

        // Turn 2 -> 3: Launch notes (w-2)
        await screen.Activate("app.desktop_shell", 2L, "launch-notes", by: "alice");
        await os.Idle();

        var view3 = await screen.Read();
        Assert.NotNull(view3);
        Assert.Equal(3L, view3.Generation);
        Assert.Contains("WindowCount: 2", view3.View.GetRawText());
        Assert.Contains("ActiveWindow: w-2", view3.View.GetRawText());

        // Turn 3 -> 4: Focus w-1
        await screen.Activate("app.desktop_shell", 3L, "focus-w-1", by: "alice");
        await os.Idle();

        var view4 = await screen.Read();
        Assert.NotNull(view4);
        Assert.Equal(4L, view4.Generation);
        Assert.Contains("ActiveWindow: w-1", view4.View.GetRawText());

        // Turn 4 -> 5: Pin w-1
        await screen.Activate("app.desktop_shell", 4L, "pin-w-1", by: "alice");
        await os.Idle();

        var view5 = await screen.Read();
        Assert.NotNull(view5);
        Assert.Equal(5L, view5.Generation);
        Assert.Contains("PinnedApps: ino,creator,calculator", view5.View.GetRawText());

        // Turn 5 -> 6: Minimize w-1 -> w-2 becomes active
        await screen.Activate("app.desktop_shell", 5L, "minimize-w-1", by: "alice");
        await os.Idle();

        var view6 = await screen.Read();
        Assert.NotNull(view6);
        Assert.Equal(6L, view6.Generation);
        Assert.Contains("ActiveWindow: w-2", view6.View.GetRawText());
    }

    [Fact]
    public async Task T4_DesktopShell_WindowClose_CleansUpActiveWindow_AndPreservesState()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-close");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        // Launch w-1, w-2
        await screen.Activate("app.desktop_shell", 1L, "launch-calculator", by: "alice");
        await os.Idle();
        await screen.Activate("app.desktop_shell", 2L, "launch-notes", by: "alice");
        await os.Idle();

        // Close w-2 (active window reverts to w-1)
        await screen.Activate("app.desktop_shell", 3L, "close-w-2", by: "alice");
        await os.Idle();

        var view4 = await screen.Read();
        Assert.NotNull(view4);
        Assert.Equal(4L, view4.Generation);
        Assert.Contains("WindowCount: 1", view4.View.GetRawText());
        Assert.Contains("ActiveWindow: w-1", view4.View.GetRawText());

        // Close w-1 (active window becomes none)
        await screen.Activate("app.desktop_shell", 4L, "close-w-1", by: "alice");
        await os.Idle();

        var view5 = await screen.Read();
        Assert.NotNull(view5);
        Assert.Equal(5L, view5.Generation);
        Assert.Contains("WindowCount: 0", view5.View.GetRawText());
        Assert.Contains("ActiveWindow: none", view5.View.GetRawText());
    }

    [Fact]
    public async Task T4_DesktopShell_WindowAndTabCaps_EnforcesMaxEightWindowsAndEightTabs()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-caps");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        // Open 8 windows sequentially
        for (long gen = 1; gen <= 8; gen++)
        {
            await screen.Activate("app.desktop_shell", gen, "launch-calculator", by: "alice");
            await os.Idle();
        }

        var view8 = await screen.Read();
        Assert.NotNull(view8);
        Assert.Equal(9L, view8.Generation);
        Assert.Contains("WindowCount: 8", view8.View.GetRawText());

        // Launch 9th window: throws InvalidOperationException
        var exWindowCap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.desktop_shell", 9L, "launch-calculator", by: "alice"));
        Assert.Contains("Maximum window cap (8) reached.", exWindowCap.Message);

        // Tab cap: add 7 additional tabs to w-1 (total 8 tabs)
        long currentGen = 9L;
        for (int i = 2; i <= 8; i++)
        {
            await screen.Activate("app.desktop_shell", currentGen, "tab-w-1", by: "alice");
            await os.Idle();
            currentGen++;
        }

        // 9th tab throws InvalidOperationException
        var exTabCap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.desktop_shell", currentGen, "tab-w-1", by: "alice"));
        Assert.Contains("Maximum tab cap (8) reached.", exTabCap.Message);
    }

    [Fact]
    public async Task T4_DesktopShell_StatePersistence_ReactivationSurvivesTurnSequences()
    {
        var os = _fixture.OS;
        os.Register<DesktopShellScreen>();

        var brain = NewBrain("desktop-persist");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.desktop_shell", "default");
        var screen = os.Get<IScreen>(screenId);

        await screen.Activate("app.desktop_shell", 1L, "launch-calculator", by: "alice");
        await os.Idle();
        await screen.Activate("app.desktop_shell", 2L, "pin-w-1", by: "alice");
        await os.Idle();

        // Deactivate and reactivate neuron to verify true durable seat persistence across restarts
        await os.Deactivate(screenId);
        await os.Activate(screenId);
        await os.Idle();

        var restored = await screen.Read();
        Assert.NotNull(restored);
        Assert.Equal(1L, restored.Generation);
        Assert.Contains("WindowCount: 1", restored.View.GetRawText());
        Assert.Contains("PinnedApps: ino,creator,calculator", restored.View.GetRawText());
        Assert.Contains("TurnCount: 2", restored.View.GetRawText());
    }
}
