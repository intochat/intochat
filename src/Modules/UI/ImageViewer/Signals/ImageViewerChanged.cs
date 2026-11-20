using DigitalBrain;

namespace IntoChat.OS.Modules.UI.ImageViewer.Signals;

[Signal("ui.image-viewer-changed")]
public sealed record ImageViewerChanged(string ViewerId, int Version, double Zoom, int RotationDegrees) : Signal;
