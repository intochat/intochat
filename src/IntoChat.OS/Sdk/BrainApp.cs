using System.ComponentModel;

namespace IntoChat.Sdk;

/// <summary>Base class for single-brain-context sandboxed apps with typed durable state and user settings.</summary>
public abstract class BrainApp<TState, TSettings>
    where TState : class, new()
    where TSettings : class, new()
{
    public ISandboxBrain Brain { get; protected set; } = default!;
    public TState State { get; protected set; } = new();
    public TSettings Settings { get; protected set; } = new();

    public virtual DigitalBrain.UI.View? Render() => null;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Bind(ISandboxBrain brain, TState state, TSettings settings)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        Brain = brain;
        State = state;
        Settings = settings;
    }
}
