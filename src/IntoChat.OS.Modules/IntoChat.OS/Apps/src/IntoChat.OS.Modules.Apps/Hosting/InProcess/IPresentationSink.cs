using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Hosting.InProcess;

public interface IPresentationSink
{
    Task Present(string owner, View? side, View? main, CancellationToken ct);
}
