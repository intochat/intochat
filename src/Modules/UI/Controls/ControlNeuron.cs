using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>
/// The base of every control neuron. A control stores only what the user changes; its setup (label, enabled, content) is the props
/// its parent writes on the slot, and the parent refuses commands to a disabled slot, so a control has no enabled check of its own.
/// When the props carry the value (a controlled control: the parent owns it), a command names the absolute value it asks for and the
/// control signals that value every time, without comparing it with the props (which may be a render behind); it stores nothing, the
/// parent applies the value idempotently and re-renders the props, and the client draws the props' value.
/// </summary>
/// <typeparam name="TState">What the user changes, stored by the control.</typeparam>
/// <typeparam name="TProps">What the parent decides, written on the slot (it names the props record the catalog and the client read).</typeparam>
public abstract class ControlNeuron<TState, TProps> : Screen<TState>, IControl<TProps> where TState : class, new() where TProps : ControlProps
{
    /// <summary>The key the control draws its view under: the last segment of its address (a slot's child key).</summary>
    protected string Key => ControlKeys.Of(Id);

    /// <summary>The props the parent routed with the running command; null for a command sent to the control directly.</summary>
    protected TProps? Props => CommandProps is { } json ? (TProps?)RuntimeProtocol.Deserialize(json, typeof(TProps)) : null;

    /// <summary>Sets an on/off value: controlled (<paramref name="controlled"/> set) it signals <paramref name="next"/> every time; otherwise it stores and signals a change.</summary>
    private protected Task SetOn(bool? controlled, bool current, bool next, Func<bool, TState> store)
        => controlled is not null ? Publish(new Toggled(next))
            : next == current ? Task.CompletedTask : Show(store(next), new Toggled(next));

    /// <summary>Selects an id: controlled (<paramref name="controlled"/> set) it signals <paramref name="next"/> every time; otherwise it stores and signals a change.</summary>
    private protected Task SelectId(string? controlled, string? current, string next, Func<string, TState> store)
        => controlled is not null ? Publish(new SelectionChanged(next))
            : next == current ? Task.CompletedTask : Show(store(next), new SelectionChanged(next));
}
