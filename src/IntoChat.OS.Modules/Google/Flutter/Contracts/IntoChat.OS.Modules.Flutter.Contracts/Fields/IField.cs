using DigitalBrain.Contracts;
using DigitalBrain;
using DigitalBrain.State;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter.Fields;

/// <summary>
/// The value of one <see cref="DigitalBrain.UI.Field"/> of an app view, addressed <c>{brain}/apps/{app}/{field}</c> (the field
/// node's <c>Name</c>). The view carries no value: a client reads it here, a person's input lands here, and the owning app
/// watches <see cref="FieldChanged"/> and reads the value back. Only the owning app configures, sets or takes it. A secret kind
/// never persists its value, only that one is set.
/// </summary>
[NeuronAlias("ui.field"), DefaultNeuronType(UIVocabulary.FieldType)]
public interface IField
{
    /// <summary>Sets what the field holds; the view neuron does this for its own app's fields on publication. A secret kind
    /// keeps only the set marker; a secret that becomes plain starts empty.</summary>
    Task Configure(FieldKind kind);

    /// <summary>Pre-fills (or clears, with null) the value: the owning app does this in its turn.</summary>
    Task Set(string? value);

    /// <summary>A person's input; journals <see cref="FieldChanged"/>. Refused before the field is configured.</summary>
    Task Input(string value);

    /// <summary>The owning app takes a secret field's value once: it is held in memory only (never persisted or journalled) and
    /// cleared by the take or by deactivation; null when none is held.</summary>
    Task<string?> TakeSecret();

    [DigitalBrain.ReadOnlyCall] Task<FieldState> Read();
}

[ExportableState]
public sealed class FieldState
{
    /// <summary>The owning app's key, <c>{brain}/apps/{app}</c>.</summary>
    public string Owner { get; set; } = "";
    /// <summary>The field's name within its owner.</summary>
    public string Name { get; set; } = "";
    public FieldKind Kind { get; set; } = FieldKind.Text;
    /// <summary>The value; for a secret kind only <c>"••••••••"</c> when one is set.</summary>
    public string? Value { get; set; }
    public long Version { get; set; }
}

/// <summary>The field changed; the value is read from the field (a secret's never travels in a signal).</summary>
[Signal("ui.field-changed")]
public sealed record FieldChanged(string Owner, string Name, long Version) : DigitalBrain.Signal;

/// <summary>A field received its first kind; nothing a watcher of <see cref="FieldChanged"/> reads has changed.</summary>
[Signal("ui.field-configured")]
public sealed record FieldConfigured(string Owner, string Name, FieldKind Kind) : DigitalBrain.Signal;
