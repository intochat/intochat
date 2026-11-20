using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Form;

[NeuronAlias("ui.form")]
[DefaultNeuronType("ui.form")]
public interface IForm : IUiInteractiveControl
{
    Task<UiFormState> Configure(
        string title,
        IReadOnlyList<FormField> fields,
        IReadOnlyDictionary<string, string>? initialValues = null,
        string submitLabel = "Save",
        bool isEnabled = true);

    /// <summary>Updates a non-secret field value. Refuses secret fields so plaintext never reaches the journal.</summary>
    Task<UiFormState> SetValue(string fieldId, string value);

    /// <summary>Records an opaque vault <see cref="UiSecretRef"/> handle for a secret field.</summary>
    Task<UiFormState> SetSecretRef(string fieldId, UiSecretRef secretRef);

    /// <summary>Validates required fields and emits <see cref="Signals.FormSubmitted"/>.</summary>
    Task<UiFormState> Submit(IReadOnlyDictionary<string, string>? values = null);

    Task<UiFormState> Reset();

    [ReadOnlyCall]
    Task<UiFormState> Read();
}
