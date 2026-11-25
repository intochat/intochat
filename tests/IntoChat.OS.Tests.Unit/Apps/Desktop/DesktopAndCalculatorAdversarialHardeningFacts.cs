using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;
using Xunit;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

[Collection(nameof(RecordingApp))]
public sealed class DesktopAndCalculatorAdversarialHardeningFacts
{
    [Fact]
    public async Task DesktopApp_WindowLimit_CapsAt8Windows_RejectsNinthWindow()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly);

        var desktop = await os.Install<DesktopApp>();

        // Open 8 distinct apps sequentially
        string[] appsToOpen =
        [
            "ino", "calculator", "creator", "tasks", "gallery", "markdraw", "friends", "settings"
        ];

        foreach (var app in appsToOpen)
        {
            await desktop.Request("open", new DesktopOpen(app));
            await os.Idle();
        }

        // Verify exactly 8 windows are open
        Assert.Equal(8, desktop.App.State.Windows.Length);

        // Attempt to open a 9th app: "photocraft"
        await desktop.Request("open", new DesktopOpen("photocraft"));
        await os.Idle();

        // 9th window is rejected; total window count remains capped at 8
        Assert.Equal(8, desktop.App.State.Windows.Length);
        Assert.DoesNotContain(desktop.App.State.Windows, w => w.Tabs.Any(t => t.Id == "photocraft"));
    }

    [Fact]
    public async Task DesktopApp_TabLimit_CapsAt8TabsPerWindow_RejectsNinthTab()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly);

        var desktop = await os.Install<DesktopApp>();

        // Open calculator window
        await desktop.Request("open", new DesktopOpen("calculator"));
        await os.Idle();

        var calcWin = desktop.App.State.Windows.First(w => w.Tabs.Any(t => t.Id == "calculator"));
        var winId = calcWin.Id;

        // Window starts with 1 tab. Add 7 more tabs (total 8 tabs)
        for (var i = 0; i < 7; i++)
        {
            await desktop.Request("newtab", new DesktopNewTab(winId));
            await os.Idle();
        }

        var fullWin = desktop.App.State.Windows.First(w => w.Id == winId);
        Assert.Equal(8, fullWin.Tabs.Length);

        // Attempt to add a 9th tab to the same window
        await desktop.Request("newtab", new DesktopNewTab(winId));
        await os.Idle();

        // Window remains strictly capped at 8 tabs
        var cappedWin = desktop.App.State.Windows.First(w => w.Id == winId);
        Assert.Equal(8, cappedWin.Tabs.Length);
    }

    [Fact]
    public async Task DesktopApp_PinInvariant_InoAndDesktop_CannotBeUnpinned()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly);

        var desktop = await os.Install<DesktopApp>();

        // Verify initial pinned apps contain ino
        Assert.Contains("ino", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);

        // Attempt to unpin ino
        await desktop.Request("unpin", new DesktopUnpinApp("ino"));
        await os.Idle();

        // Invariant: ino remains pinned
        Assert.Contains("ino", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);

        // Attempt to unpin desktop
        await desktop.Request("unpin", new DesktopUnpinApp("desktop"));
        await os.Idle();

        // Desktop cannot be pinned or unpinned
        Assert.DoesNotContain("desktop", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DesktopApp_Close_NonExistentWindow_ExecutesIdempotentlyWithoutException()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<TestViewportNeuron>();
        os.Register<TestWorkspaceNeuron>();
        os.RegisterCatalog();
        os.Register(typeof(DesktopApp).Assembly);

        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("open", new DesktopOpen("calculator"));
        await os.Idle();
        var winCount = desktop.App.State.Windows.Length;

        // Close non-existent target
        await desktop.Request("close", new DesktopClose("non-existent-win-999"));
        await os.Idle();

        // Windows count unchanged
        Assert.Equal(winCount, desktop.App.State.Windows.Length);
    }

    [Fact]
    public async Task CalculatorApp_DivisionByZero_EvaluatesToError_AndResetsOnNextDigit()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);

        var calc = await os.Install<CalculatorApp>();

        // 5 / 0 =
        await calc.Request("press", new CalculatorPress("5"));
        await os.Idle();
        await calc.Request("press", new CalculatorPress("/"));
        await os.Idle();
        await calc.Request("press", new CalculatorPress("0"));
        await os.Idle();
        await calc.Request("press", new CalculatorPress("="));
        await os.Idle();

        // Division by zero evaluates to "Error"
        Assert.Equal("Error", calc.App.State.Expression);

        // Pressing next digit clears "Error" and starts fresh
        await calc.Request("press", new CalculatorPress("7"));
        await os.Idle();

        Assert.Equal("7", calc.App.State.Expression);
    }

    [Fact]
    public async Task CalculatorApp_MalformedArithmetic_EvaluatesToError()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);

        var calc = await os.Install<CalculatorApp>();

        // Malformed sequence: + * =
        await calc.Request("press", new CalculatorPress("+"));
        await os.Idle();
        await calc.Request("press", new CalculatorPress("*"));
        await os.Idle();
        await calc.Request("press", new CalculatorPress("="));
        await os.Idle();

        Assert.Equal("Error", calc.App.State.Expression);

        // Clear resets display
        await calc.Request("press", new CalculatorPress("C"));
        await os.Idle();
        Assert.Equal(string.Empty, calc.App.State.Expression);
    }

    [Fact]
    public async Task CalculatorApp_AddOperation_MalformedAndExtremeInputs_ReturnError()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);

        var calc = await os.Install<CalculatorApp>();

        // Invalid term
        await calc.Request("add", new CalculatorAdd("abc+xyz"));
        await os.Idle();
        Assert.Equal("Error", calc.App.State.Expression);

        // Single number (not a sum)
        await calc.Request("add", new CalculatorAdd("42"));
        await os.Idle();
        Assert.Equal("Error", calc.App.State.Expression);

        // Overflow sum
        await calc.Request("add", new CalculatorAdd("79228162514264337593543950335+79228162514264337593543950335"));
        await os.Idle();
        Assert.Equal("Error", calc.App.State.Expression);
    }

    [Fact]
    public async Task CalculatorApp_HistoryCap_EnforcesMax8Entries()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(CalculatorApp).Assembly);

        var calc = await os.Install<CalculatorApp>();

        // Perform 12 calculations
        for (var i = 1; i <= 12; i++)
        {
            await calc.Request("press", new CalculatorPress("C"));
            await os.Idle();
            await calc.Request("press", new CalculatorPress($"{i}"));
            await os.Idle();
            await calc.Request("press", new CalculatorPress("+"));
            await os.Idle();
            await calc.Request("press", new CalculatorPress("1"));
            await os.Idle();
            await calc.Request("press", new CalculatorPress("="));
            await os.Idle();
        }

        // History is strictly capped at at most 8 items
        Assert.NotNull(calc.App.State.History);
        Assert.True(calc.App.State.History.Length <= 8, $"History length was {calc.App.State.History.Length}, expected <= 8");
    }
}
