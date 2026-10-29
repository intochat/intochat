/*
# FilmCraft
Role: Example
Shipped: false
Operations: open

The folder script still connects the editor. Opening presents the connected status and does not record an error.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Baseline;

[DigitalBrain.Signal("filmcraft.open")]
public sealed record FilmCraftOpen : DigitalBrain.Signal;

[DigitalBrain.App("filmcraft")]
public sealed class FilmCraftBaseline(IPresenter presenter) : DigitalBrain.App
{
    public const string Connected = "FilmCraft tools connected.";

    [DigitalBrain.On]
    public Task Open(FilmCraftOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Present(presenter.Present, static () => null, static () => new View("FilmCraft", new Text(Connected)));
        return Task.CompletedTask;
    }
}
