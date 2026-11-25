using System.Text.Json;

namespace DigitalBrain.Units;

/// <summary>What a published view's keyed nodes send.</summary>
public static class PublishedViews
{
    /// <summary>
    /// The command the node keyed <paramref name="key"/> in <paramref name="view"/> sends (a field's is its submit, an info bar's its
    /// close), with <paramref name="value"/>, a field's text, joined to its args as "value". Its target is relative to the screen whose
    /// view it is; a caller sends it through that screen's <see cref="IScreen.Dispatch"/>, so a child is reached through its parent.
    /// </summary>
    /// <exception cref="ActivationRefusedException">No node has the key (<see cref="ActivationRefusedException.UnknownNode"/>) or it sends
    /// no command (<see cref="ActivationRefusedException.NoEmit"/>).</exception>
    /// <exception cref="ArgumentException">A value is given and the command's args are positional (an array or a scalar).</exception>
    public static DigitalBrain.UI.Command CommandAt(this PublishedView view, string key, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var node = ViewNodes.Find(view.View, key) ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownNode);
        var command = ViewNodes.CommandOf(node) ?? throw new ActivationRefusedException(ActivationRefusedException.NoEmit);
        if (value is null) { return command; }
        if (command.Args is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException($"The command of '{key}' has positional args; a field's value joins object args only.", nameof(value));
        }
        return command with { Args = ViewNodes.Payload(command.Args, value, by: null) };
    }
}
