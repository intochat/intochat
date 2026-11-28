using DigitalBrain.UI;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

public sealed class DesktopProjectionAndRendererFacts
{
    [Fact]
    public void DesktopViewRenderer_MatchesDesktopProjectionBuilder_AcrossWindowsTabsPinsFavouritesWidgetsAndViewports()
    {
        var state = new DesktopAppState() with
        {
            LayoutMode = "spatial-grid",
            StylePreset = "liquid-glass",
            BarLayout = "bottom",
            FocusedViewId = "ino-2",
            Windows =
            [
                new WorkspaceWindow(
                    "w-1",
                    [
                        new WorkspaceViewRef("calculator", "local/apps/calculator", "main", "default"),
                        new WorkspaceViewRef("creator", "local/apps/creator", "main", "default"),
                    ],
                    "creator"),
                new WorkspaceWindow(
                    "w-2",
                    [
                        new WorkspaceViewRef("ino", "local/apps/ino", "main", "default"),
                        new WorkspaceViewRef("ino-2", "local/apps/ino", "main", "default"),
                    ],
                    "ino-2"),
            ],
            Pins =
            [
                new WorkspaceViewRef("creator", "local/apps/creator", "main", "default"),
                new WorkspaceViewRef("customruntime", "local/apps/customruntime", "main", "default"),
            ],
            Favourites =
            [
                new WorkspaceViewRef("creator", "local/apps/creator", "main", "default"),
            ],
            WidgetsText = "launcher · signal · clock",
            DeployedUnits = ["helloworld"],
        };

        var projection = DesktopProjectionBuilder.Build(state, "local");
        var tabStrips = new Dictionary<string, TabStripProps>(StringComparer.Ordinal);
        var root = DesktopViewRenderer.Render(projection, (key, props) =>
        {
            tabStrips[key] = props;
            return new Text($"tabstrip:{key}");
        });

        var nodes = Flatten(root).ToArray();

        // 1. Catalog tiles match projection.Tiles 1:1
        var catalogTiles = nodes.OfType<AppTileNode>()
            .Where(t => t.Key.StartsWith("catalog-", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(projection.Tiles.Select(static t => t.Id), catalogTiles.Select(static t => t.AppId));
        Assert.Equal(projection.Tiles.Select(static t => t.Title), catalogTiles.Select(static t => t.Title));

        // 2. Dock pins and unit tiles derive strictly from projection.Pins & projection.Tiles
        var dockTiles = nodes.OfType<AppTileNode>()
            .Where(t => t.Key.StartsWith("dock-", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(dockTiles, t => t.Key == "dock-pin-creator" && t.AppId == "creator");
        Assert.Contains(dockTiles, t => t.Key == "dock-pin-customruntime" && t.AppId == "customruntime" && t.Title == "Customruntime");
        Assert.Contains(dockTiles, t => t.Key == "dock-unit-helloworld" && t.AppId == "helloworld" && t.AppKind == "unit");

        // 3. Every projected window has matching viewport embed, chrome buttons, and tab strip
        var embeds = nodes.OfType<NeuronRef>().Where(static r => r.Kind == "viewport").ToArray();
        Assert.Equal(projection.Windows.Select(static w => w.ViewportUri), embeds.Select(static e => e.NeuronUri));

        foreach (var win in projection.Windows)
        {
            var focusButton = Assert.Single(nodes.OfType<Button>(), b => b.Key == $"focus-{win.Id}");
            Assert.Equal(win.Title, focusButton.Label);

            var starButton = Assert.Single(nodes.OfType<Button>(), b => b.Key == $"star-{win.Id}");
            Assert.Equal(win.IsFavourite ? "Remove from favourites" : "Add to favourites", starButton.Label);

            var expectedActiveIndex = Array.FindIndex(win.Tabs, static t => t.Selected);
            Assert.True(tabStrips.TryGetValue(DesktopApp.WindowTabsKey(win.Id), out var strip));
            Assert.Equal($"{win.Id}/{expectedActiveIndex}", strip.SelectedId);
            Assert.Equal($"{win.Title} tabs", strip.AutomationName);
            Assert.Equal(win.Tabs.Select(static t => t.Title), strip.Items.Select(static i => i.Label));
        }

        // w-1 is creator (pinned -> Unpin button); w-2 is ino (never shows Pin/Unpin)
        Assert.Contains(nodes.OfType<Button>(), b => b.Key == "unpin-w-1" && b.Label == "Unpin");
        Assert.DoesNotContain(nodes.OfType<Button>(), b => b.Key is "pin-w-2" or "unpin-w-2");

        // Second tab of ino ("ino-2") resolves to "Ino" in both projection and rendered tab strip (no drift)
        Assert.Equal(["Ino", "Ino"], tabStrips["tabs-w-2"].Items.Select(static i => i.Label));

        // 4. Open widgets text matches projection.OpenWidgets
        Assert.Contains(nodes.OfType<Text>(), t => t.Value == string.Join(" · ", projection.OpenWidgets));
    }

    [Fact]
    public async Task OptionalSlotHelpers_SwallowNonCancellationErrorsAndPropagateCancellation()
    {
        var logger = new RecordingLogger<DesktopApp>();
        var services = new ServiceCollection()
            .AddSingleton<ILogger<DesktopApp>>(logger)
            .BuildServiceProvider();
        var app = ActivatorUtilities.CreateInstance<DesktopApp>(services);

        var readFallback = await app.TryReadOptionalSlotAsync<string>(
            () => throw new InvalidOperationException("slot offline"),
            fallback: "default-value",
            context: "Optional read failed in test.");
        Assert.Equal("default-value", readFallback);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Optional read failed in test.", StringComparison.Ordinal));

        var invoked = await app.TryInvokeOptionalSlotAsync(
            () => throw new TimeoutException("slot timed out"),
            context: "Optional invoke failed in test.");
        Assert.False(invoked);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Optional invoke failed in test.", StringComparison.Ordinal));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            app.TryReadOptionalSlotAsync<string>(
                () => throw new OperationCanceledException("cancelled"),
                fallback: "unused",
                context: "Should not swallow cancellation."));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            app.TryInvokeOptionalSlotAsync(
                () => throw new OperationCanceledException("cancelled"),
                context: "Should not swallow cancellation."));
    }

    private static IEnumerable<Node> Flatten(Node node)
    {
        yield return node;
        var children = node switch
        {
            Row r => r.Children,
            Column c => c.Children,
            Surface sf => sf.Children,
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
