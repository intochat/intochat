using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// What Ino's sphere shows. Disconnected is the shell's own (its watch dropped); the presence neuron never sets it.
public enum PresenceState { Idle, Listening, Working, Searching, Waiting, Attention, Paused, Disconnected }

// Critical is the platform's: a focus a model writes with it is dropped.
public enum FocusTone { Info, Warning, Success, Critical }

// What the shell offers beside a focus, e.g. Kind "open" with Argument an app id.
public sealed record FocusAction(string Kind, string Argument);

// Where Ino wants the person to look: Target is "kind:name" for a component or "window:{appId}" for a window.
public sealed record Focus(string Target, FocusTone Tone, string Reason, FocusAction? Action);

// The journal's fold for the sphere; a wire record mirrored in Dart, so its members only ever grow.
public sealed record Presence(
    PresenceState State,
    string? FlowId,
    string? Step,
    string? FlowType,
    string? NeuronKind,
    string? TraceId,
    Focus? Focus,
    string? Reason,
    long Revision)
{
    public Presence() : this(PresenceState.Idle, null, null, null, null, null, null, null, 0) { }
}

[DigitalBrain.Signal("ino.presence.changed")]
public sealed record PresenceChanged(long Revision) : DigitalBrain.Signal;

// One per brain; the shell watches it and draws the sphere from Read.
[NeuronAlias("ino.presence"), DefaultNeuronType("ino.presence")]
public interface IPresence
{
    const string DefaultKey = "ino/presence";

    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "ino.presence", DefaultKey);

    [DigitalBrain.ReadOnlyCall]
    Task<Presence> Read(CancellationToken ct = default);
}
