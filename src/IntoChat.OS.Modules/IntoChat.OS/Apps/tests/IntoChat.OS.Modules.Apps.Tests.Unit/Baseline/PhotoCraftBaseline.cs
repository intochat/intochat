/*
# PhotoCraft
Role: Example
Shipped: false
Operations: open, edit, preview

The folder script still runs the editor. Empty edit presents a status and does not throw or record an error.
*/
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Baseline;

[DigitalBrain.Signal("photocraft.open")]
public sealed record PhotoCraftOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.edit")]
public sealed record PhotoCraftEdit(string? Request) : DigitalBrain.Signal;

[DigitalBrain.Signal("photocraft.preview")]
public sealed record PhotoCraftPreview : DigitalBrain.Signal;

[DigitalBrain.App("photocraft")]
public sealed class PhotoCraftBaseline(IPresenter presenter) : DigitalBrain.App
{
    public const string Connected = "PhotoCraft tools connected.";
    public const string Idle = "Nothing to edit.";
    public string Status { get; private set; } = Connected;

    [DigitalBrain.On]
    public Task Open(PhotoCraftOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show(Connected);
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public Task Edit(PhotoCraftEdit request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show(string.IsNullOrWhiteSpace(request.Request) ? Idle : "Editing: " + request.Request);
        return Task.CompletedTask;
    }

    [DigitalBrain.On]
    public Task Preview(PhotoCraftPreview request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Show(Status);
        return Task.CompletedTask;
    }

    private void Show(string status)
    {
        Status = status;
        Present(presenter.Present, static () => null, () => new View("PhotoCraft", new Text(Status)));
    }
}
