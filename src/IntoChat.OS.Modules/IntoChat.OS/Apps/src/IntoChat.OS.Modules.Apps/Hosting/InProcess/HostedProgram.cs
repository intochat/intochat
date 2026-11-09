using DigitalBrain.Apps.Hosting;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Hosting.InProcess;

internal sealed class HostedProgram(
    string owner,
    string hash,
    AppTurns turns,
    IReadOnlyDictionary<string, string> settings,
    BehaviorView[] behaviors) : IPresenter
{
    private View? _presentedSide;
    private View? _presentedMain;

    public string Owner { get; } = owner;

    public string Hash { get; } = hash;

    public AppTurns Turns { get; } = turns;

    public IReadOnlyDictionary<string, string> Settings { get; } = settings;

    public BehaviorView[] Behaviors { get; } = behaviors;

    public string? LastRootView { get; set; }

    public string? LastCause { get; set; }

    public void Present(View? side, View? main)
    {
        _presentedSide = side ?? _presentedSide;
        _presentedMain = main ?? _presentedMain;
    }

    public void Toast(Toast toast)
    {
    }

    public (View? Side, View? Main) ConsumePresented() => (_presentedSide, _presentedMain);
}
