/*
# Form

A control neuron: a set of text fields submitted at once. Typed text stays in the client until Submit, which carries every editable
field's value, typed or not. The form keeps the last submitted values and signals Submitted with all of them; when the parent's
props carry field values (a controlled form) it stores nothing and only signals. Its fields, submit label, enabled and accessible name are props its parent writes on the
child node; the parent refuses a submit naming an unknown or read-only field, a value over MaxLength and a non-number in a number field.
A secret field never travels as a value: the client stores the typed text in the person's vault and the submit carries the vault
reference in SecretRefs; the parent refuses a secret field's key inside Values and anything in SecretRefs that is not a reference to
a secret field. The form signals the references with Submitted and keeps none of them.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A form: it keeps the last submitted values and signals <see cref="Submitted"/>.</summary>
[Neuron("control.form"), Publishes(typeof(Submitted))]
public sealed class FormNeuron : ControlNeuron<FormState, FormProps>, IFormControl
{
    [On]
    public Task Submit(Submit command)
    {
        ArgumentNullException.ThrowIfNull(command);
        // A field the submit leaves out keeps its current value (the parent's when it controls it, else the last submitted), so
        // Submitted always carries the form's current values.
        var props = Props;
        var all = new Dictionary<string, string>(State.Values, StringComparer.Ordinal);
        foreach (var field in props?.Fields ?? []) { if (field.Value is { } value) { all[field.Key] = value; } }
        foreach (var (key, value) in command.Values) { all[key] = value; }
        // Secret references are signalled once and never kept: the form draws no secret.
        var submitted = new Submitted(all, command.SecretRefs is { Count: > 0 } refs ? new Dictionary<string, string>(refs, StringComparer.Ordinal) : null);
        // Controlled (the parent owns a value): signal only; the parent stores and re-renders the props.
        return props?.Fields.Any(field => field.Value is not null) == true
            ? Publish(submitted)
            : Show(State with { Values = all }, submitted);
    }

    protected override View Render(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new FormView(Key, [.. state.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new FormValue(entry.Key, entry.Value))]));
    }
}
