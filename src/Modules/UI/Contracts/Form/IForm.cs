using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Form;

[NeuronAlias("ui.form")]
[DefaultNeuronType("ui.form")]
public interface IForm
{
    Task<FormState> Configure(
        string title,
        IReadOnlyList<FormField> fields,
        IReadOnlyDictionary<string, string>? initialValues = null,
        string submitLabel = "Save",
        bool isEnabled = true);

    /// <summary>Updates a non-secret field value. Refuses secret fields so plaintext never reaches the journal.</summary>
    Task<FormState> SetValue(string fieldId, string value);

    /// <summary>Records an opaque vault <see cref="SecretRef"/> handle for a secret field.</summary>
    Task<FormState> SetSecretRef(string fieldId, SecretRef secretRef);

    /// <summary>Validates required fields and emits <see cref="Signals.FormSubmitted"/>.</summary>
    Task<FormState> Submit(IReadOnlyDictionary<string, string>? values = null);

    Task<FormState> Reset();

    [ReadOnlyCall]
    Task<FormState> Read();
}
