using DigitalBrain;
using DigitalBrain.UI;

namespace APP_NAMESPACE;

/// <summary>Asks the app to show its start view.</summary>
[Signal("APP_ID.opened")]
public sealed record Opened : Signal;

/// <summary>
/// The APP_CLASS app. What it does is written in the spec comment next to this file; every operation described there is one
/// <c>[On]</c> method here. The app id is frozen once people install it.
/// </summary>
[App("APP_ID")]
public sealed class APP_CLASS(IPresenter presenter) : App
{
    private int _opened;

    /// <summary>Shows the start view.</summary>
    [On]
    public Task Open(Opened signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _opened++;
        _ = Present(presenter.Present,
            () => new View("APP_CLASS", new Collection([])),
            () => new View("APP_CLASS", new Column([new Text("Welcome to APP_CLASS."), new Text($"Opened {_opened} times.")])));
        return Task.CompletedTask;
    }
}
