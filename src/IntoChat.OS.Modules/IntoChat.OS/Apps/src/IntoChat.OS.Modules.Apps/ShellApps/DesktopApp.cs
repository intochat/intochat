/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow

The embedded folder scripts publish the Flutter desktop. These operations present a status for the baseline.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Signal("desktop.start")]
public sealed record DesktopStart : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.layout")]
public sealed record DesktopLayoutChoice : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.open")]
public sealed record DesktopOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("desktop.narrow")]
public sealed record DesktopNarrow : DigitalBrain.Signal;

[DigitalBrain.App("desktop")]
public sealed class DesktopApp(IPresenter presenter) : DigitalBrain.App
{
    public static DigitalBrain.AppManifest Manifest { get; } = new(
        "desktop", "Desktop", "0.3.0", [], new DigitalBrain.AppRequirements([]));

    public static AppBundle Bundle { get; } = ShellPrograms.Load("Desktop");

    [DigitalBrain.On]
    public Task Start(DesktopStart request) => Show(request);

    [DigitalBrain.On]
    public Task Layout(DesktopLayoutChoice request) => Show(request);

    [DigitalBrain.On]
    public Task Open(DesktopOpen request) => Show(request);

    [DigitalBrain.On]
    public Task Narrow(DesktopNarrow request) => Show(request);

    private Task Show(DigitalBrain.Signal request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Present(presenter.Present, static () => null, static () => new View("Desktop", new Text("Desktop")));
        return Task.CompletedTask;
    }
}
