/*
# Form

A control neuron: a set of text fields submitted at once. Typed text stays in the client until Submit, which carries every editable
field's value, typed or not. The form keeps the last submitted non-secret values and signals Submitted with all of them, secrets
included; it never stores or draws a secret. Its fields, submit label, enabled and accessible name are props its parent writes on the
slot; the parent refuses a submit naming an unknown or read-only field, a value over MaxLength, a non-number in a number field, or a
secrets list that does not name exactly the secret fields carried.
*/
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;

namespace IntoChat.OS.Apps.Controls;

/// <summary>A form: it keeps the last submitted values and signals <see cref="Submitted"/>.</summary>
[Neuron("control.form"), Emits(typeof(Submitted))]
public sealed class FormNeuron : ControlNeuron<FormState, FormProps>
{
    [On]
    public Task Submit(Submit command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var secrets = new HashSet<string>(command.Secrets ?? [], StringComparer.Ordinal);
        // A field the submit leaves out keeps its last submitted value, so Submitted always carries the form's current values.
        var all = new Dictionary<string, string>(State.Values, StringComparer.Ordinal);
        foreach (var (key, value) in command.Values) { all[key] = value; }
        var kept = all.Where(entry => !secrets.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return Show(State with { Values = kept }, new Submitted(all));
    }

    protected override View Render(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(Key, new FormView(Key, [.. state.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new FormValue(entry.Key, entry.Value))]));
    }
}
