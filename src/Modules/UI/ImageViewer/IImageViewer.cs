using IntoChat.OS.Modules.UI.Core;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.ImageViewer;

[GenerateSerializer, Alias("ui.image-viewer.state")]
public sealed record ImageViewerState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] string SourceUrl,
    [property: Id(3)] string? Caption,
    [property: Id(4)] double Zoom,
    [property: Id(5)] int RotationDegrees,
    [property: Id(6)] string FitMode)
{
    public ImageViewerState() : this(0, "Image", "", null, 1.0, 0, "contain") { }
}

[NeuronAlias("ui.image-viewer")]
[DefaultNeuronType("ui.image-viewer")]
public interface IImageViewer : IUiInteractiveControl
{
    Task<ImageViewerState> Configure(string title, string sourceUrl, string? caption = null, string fitMode = "contain");
    Task<ImageViewerState> SetZoom(double zoom);
    Task<ImageViewerState> Rotate(int deltaDegrees = 90);
    Task<ImageViewerState> ResetView();
    [ReadOnlyCall] Task<ImageViewerState> Read();
}
