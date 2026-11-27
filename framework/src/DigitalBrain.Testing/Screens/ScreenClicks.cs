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

    /// <summary>Sends the command of the node keyed <paramref name="key"/> in the view of this screen's child at <paramref name="childPath"/>
    /// ("a" or "a/b") through this screen, as a client acts on a child: a child takes commands only from its parent, whose gates apply on the way.</summary>
    /// <exception cref="ActivationRefusedException">The child has no view, no node has the key, it sends no command, or a parent on the path or the child refuses it.</exception>
    public static async Task ClickChild(this IScreen screen, string childPath, string key, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(childPath);
        var view = await screen.ReadChild(childPath).ConfigureAwait(false) ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownNode, "The child has no view yet.");
        var command = view.CommandAt(key, value);
        string target;
        if (command.Target == "/")
        {
            target = ".";
        }
        else if (command.Target.StartsWith('/'))
        {
            target = command.Target[1..];
        }
        else
        {
            var inner = command.Target.StartsWith("./", StringComparison.Ordinal) ? command.Target[2..] : command.Target;
            target = inner == "." ? childPath : childPath + "/" + inner;
        }
        await screen.Dispatch(command with { Target = target }).ConfigureAwait(false);
    }
}
