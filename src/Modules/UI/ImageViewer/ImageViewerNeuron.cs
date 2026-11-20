using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.ImageViewer.Signals;

namespace IntoChat.OS.Modules.UI.ImageViewer;

[Neuron("ui.image-viewer")]
internal sealed class ImageViewerNeuron : Neuron<ImageViewerState>, IImageViewer
{
    public async Task<ImageViewerState> Configure(string title, string sourceUrl, string? caption = null, string fitMode = "contain")
    {
        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "Image",
            SourceUrl = sourceUrl ?? "",
            Caption = caption,
            FitMode = fitMode is "cover" or "fill" or "contain" ? fitMode : "contain",
            Zoom = 1.0,
            RotationDegrees = 0,
        };
        await Save(next, new ImageViewerChanged(Id.FullKey(), next.Version, next.Zoom, next.RotationDegrees));
        return next;
    }

    public async Task<ImageViewerState> SetZoom(double zoom)
    {
        var clamped = Math.Round(Math.Clamp(zoom, 0.25, 4.0), 2);
        if (Math.Abs(clamped - State.Zoom) < 0.001) { return State; }
        var next = State with { Version = State.Version + 1, Zoom = clamped };
        await Save(next, new ImageViewerChanged(Id.FullKey(), next.Version, next.Zoom, next.RotationDegrees));
        return next;
    }

    public async Task<ImageViewerState> Rotate(int deltaDegrees = 90)
    {
        var rot = ((State.RotationDegrees + deltaDegrees) % 360 + 360) % 360;
        var next = State with { Version = State.Version + 1, RotationDegrees = rot };
        await Save(next, new ImageViewerChanged(Id.FullKey(), next.Version, next.Zoom, next.RotationDegrees));
        return next;
    }

    public async Task<ImageViewerState> ResetView()
    {
        var next = State with { Version = State.Version + 1, Zoom = 1.0, RotationDegrees = 0 };
        await Save(next, new ImageViewerChanged(Id.FullKey(), next.Version, next.Zoom, next.RotationDegrees));
        return next;
    }

    public Task<ImageViewerState> Read() => Task.FromResult(State);
}
