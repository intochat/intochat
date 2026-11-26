using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Flutter;

/// <summary>
/// A single tab inside a desktop window in <see cref="DesktopShellProjection"/>.
/// Dart mirror: <c>DesktopTabProjection</c> in <c>ui/lib/src/core/models/desktop_projection.dart</c>.
/// </summary>
[GenerateSerializer, Alias("ui.desktop-tab-projection")]
public sealed record DesktopTabProjection(
    [property: Id(0)] string Id,
    [property: Id(1)] string AppId,
    [property: Id(2)] string Title,
    [property: Id(3)] bool Selected);

/// <summary>
/// A single open desktop window in <see cref="DesktopShellProjection"/>.
/// Dart mirror: <c>DesktopWindowProjection</c> in <c>ui/lib/src/core/models/desktop_projection.dart</c>.
/// </summary>
[GenerateSerializer, Alias("ui.desktop-window-projection")]
public sealed record DesktopWindowProjection(
    [property: Id(0)] string Id,
    [property: Id(1)] string AppId,
    [property: Id(2)] string Title,
    [property: Id(3)] string ViewportUri,
    [property: Id(4)] DesktopTabProjection[] Tabs,
    [property: Id(5)] string ActiveTabId,
    [property: Id(6)] bool IsPinned,
    [property: Id(7)] bool IsFavourite);

/// <summary>
/// Typed projection of desktop shell state for native/web desktop clients, replacing visual-tree scraping.
/// Headless <c>IScreen</c> / <c>AppSession</c> clients continue to consume <c>DesktopApp.Render</c>.
/// Dart mirror: <c>DesktopShellProjection</c> in <c>ui/lib/src/core/models/desktop_projection.dart</c>.
/// </summary>
[GenerateSerializer, Alias("ui.desktop-shell-projection")]
public sealed record DesktopShellProjection(
    [property: Id(0)] DesktopWindowProjection[] Windows,
    [property: Id(1)] string? FocusedViewId,
    [property: Id(2)] string[] Pins,
    [property: Id(3)] AppTile[] Tiles,
    [property: Id(4)] string[] OpenWidgets,
    [property: Id(5)] string StylePreset,
    [property: Id(6)] string BarLayout,
    [property: Id(7)] string LayoutMode = "smart-desktop",
    [property: Id(8)] bool IsNarrow = false);

/// <summary>
/// Read-only projection contract implemented by <c>app.desktop</c> (<c>DesktopApp</c>).
/// </summary>
[NeuronAlias("ui.desktop-shell"), DefaultNeuronType("app.desktop")]
public interface IDesktopShell
{
    const string Type = "ui.desktop-shell";
    const string DefaultKey = "default";

    static string Key(string brainId) => $"{brainId}/{DefaultKey}";

    [ReadOnlyCall]
    Task<DesktopShellProjection> ReadProjection();
}
