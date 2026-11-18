using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.UI.Controls;
using DigitalBrain.Units;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A control refused a command because it is disabled.</summary>
public sealed record ControlDisabled() : Refusal("control.disabled")
{
    /// <inheritdoc />
    public override string Message => "This control is disabled.";
}

/// <summary>
/// The base of every control neuron: one refusal path. A command a control refuses throws <see cref="ActivationRefusedException"/>
/// with the refusal's code as its reason, which is how a screen refuses an act (the edge answers 409 with that reason and journals nothing).
/// </summary>
public abstract class ControlNeuron<TState> : Screen<TState> where TState : class, IControlState, new()
{
    /// <summary>Refuses with <see cref="ControlDisabled"/> while the control is disabled.</summary>
    /// <exception cref="ActivationRefusedException">The control is disabled.</exception>
    protected void EnsureEnabled()
    {
        if (!State.IsEnabled) { throw Refused(new ControlDisabled()); }
    }

    /// <summary>The exception that refuses a command with <paramref name="refusal"/>'s code.</summary>
    protected static ActivationRefusedException Refused(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new ActivationRefusedException(refusal.Code);
    }

    /// <summary>The key the control draws its view under: the last segment of its address (a slot's child key).</summary>
    protected string Key => ControlKeys.Of(Id);
}
