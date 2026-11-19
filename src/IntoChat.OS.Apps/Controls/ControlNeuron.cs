using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;

namespace IntoChat.OS.Apps.Controls;

/// <summary>
/// The base of every control neuron. A control stores only what the user changes; its setup (label, enabled, content) is the props
/// its parent writes on the slot, and the parent refuses commands to a disabled slot, so a control has no enabled check of its own.
/// When the props carry the value (a controlled control: the parent owns it), a command only signals the requested value and the
/// control stores nothing; the parent stores it and re-renders the props, and the client draws the props' value.
/// </summary>
/// <typeparam name="TState">What the user changes, stored by the control.</typeparam>
/// <typeparam name="TProps">What the parent decides, written on the slot (it names the props record the catalog and the client read).</typeparam>
public abstract class ControlNeuron<TState, TProps> : Screen<TState>, IControl<TProps> where TState : class, new() where TProps : ControlProps
{
    /// <summary>The key the control draws its view under: the last segment of its address (a slot's child key).</summary>
    protected string Key => ControlKeys.Of(Id);

    /// <summary>The props the parent routed with the running command; null for a command sent to the control directly.</summary>
    protected TProps? Props => CommandProps is { } json ? json.Deserialize<TProps>(JsonSerializerOptions.Web) : null;

    /// <summary>Sets an on/off value: controlled (<paramref name="controlled"/> set) it only signals a change; otherwise it stores and signals.</summary>
    private protected Task SetOn(bool? controlled, bool current, bool next, Func<bool, TState> store)
        => controlled is { } owned
            ? next == owned ? Task.CompletedTask : Publish(new Toggled(next))
            : next == current ? Task.CompletedTask : Show(store(next), new Toggled(next));

    /// <summary>Selects an id: controlled (<paramref name="controlled"/> set) it only signals a change; otherwise it stores and signals.</summary>
    private protected Task SelectId(string? controlled, string? current, string next, Func<string, TState> store)
        => controlled is not null
            ? next == controlled ? Task.CompletedTask : Publish(new SelectionChanged(next))
            : next == current ? Task.CompletedTask : Show(store(next), new SelectionChanged(next));
}
