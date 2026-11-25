/*
# Form

A control neuron: a set of text fields submitted at once. Typed text stays in the client until Submit, which carries every editable
field's value, typed or not. The form keeps the last submitted values and signals Submitted with all of them; when the parent's
props carry field values (a controlled form) it stores nothing and only signals. Its fields, submit label, enabled and accessible name are props its parent writes on the
slot; the parent refuses a submit naming an unknown or read-only field, a value over MaxLength, a non-number in a number field, and every
submit to a form with a secret field (control.secretUnsupported: a submit is journaled; a vault path is owed).
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A form: it keeps the last submitted values and signals <see cref="Submitted"/>.</summary>
[Neuron("control.form"), Emits(typeof(Submitted))]
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
        // Controlled (the parent owns a value): signal only; the parent stores and re-renders the props.
        return props?.Fields.Any(field => field.Value is not null) == true
            ? Publish(new Submitted(all))
            : Show(State with { Values = all }, new Submitted(all));
    }

    protected override View Render(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new FormView(Key, [.. state.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new FormValue(entry.Key, entry.Value))]));
    }
}
