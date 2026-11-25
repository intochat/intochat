using DigitalBrain.Units;

namespace DigitalBrain.Testing;

/// <summary>Acts on a screen as a client does: it reads the current view, takes the keyed node's command and sends it through <see cref="IScreen.Dispatch"/>.</summary>
public static class ScreenClicks
{
    /// <summary>Sends the command of the node keyed <paramref name="key"/> in the screen's current view, with <paramref name="value"/>
    /// (a field's text) joined to its args as "value". The handler hears the acting person from the caller, never from the args.</summary>
    /// <exception cref="ActivationRefusedException">The screen has no view, no node has the key, it sends no command, or the screen refuses it.</exception>
    public static async Task Click(this IScreen screen, string key, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var view = await screen.Read().ConfigureAwait(false) ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownNode, "The screen has no view yet.");
        await screen.Dispatch(view.CommandAt(key, value)).ConfigureAwait(false);
    }
}
