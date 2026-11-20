using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Form.Signals;

namespace IntoChat.OS.Modules.UI.Form;

[Neuron("ui.form")]
internal sealed class FormNeuron : Neuron<UiFormState>, IForm
{
    public async Task<UiFormState> Configure(
        string title,
        IReadOnlyList<FormField> fields,
        IReadOnlyDictionary<string, string>? initialValues = null,
        string submitLabel = "Save",
        bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var currentValues = State.Values;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (string.Equals(field.Kind, FormFieldKinds.Secret, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (initialValues is not null && initialValues.TryGetValue(field.Id, out var val))
            {
                values[field.Id] = val ?? "";
            }
            else if (currentValues.TryGetValue(field.Id, out var existing))
            {
                values[field.Id] = existing;
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "",
            Fields = [.. fields],
            ValueEntries = ToValueEntries(values),
            SubmitLabel = string.IsNullOrWhiteSpace(submitLabel) ? "Save" : submitLabel,
            IsEnabled = isEnabled,
            Error = null,
        };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<UiFormState> SetValue(string fieldId, string value)
    {
        EnsureEnabled();
        var field = RequireField(fieldId);
        if (string.Equals(field.Kind, FormFieldKinds.Secret, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Field '{fieldId}' is a secret field; use SetSecretRef with a vault handle instead of plaintext.");
        }
        var clamped = value ?? "";
        if (field.MaxLength is > 0 and var max && clamped.Length > max)
        {
            clamped = clamped[..max];
        }
        var values = new Dictionary<string, string>(State.Values, StringComparer.Ordinal)
        {
            [field.Id] = clamped,
        };
        var next = State with { Version = State.Version + 1, ValueEntries = ToValueEntries(values), Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<UiFormState> SetSecretRef(string fieldId, UiSecretRef secretRef)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef.VaultKey);
        var field = RequireField(fieldId);
        var refs = new Dictionary<string, UiSecretRef>(State.SecretRefs, StringComparer.Ordinal)
        {
            [field.Id] = secretRef,
        };
        var next = State with { Version = State.Version + 1, SecretEntries = ToSecretEntries(refs), Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<UiFormState> Submit(IReadOnlyDictionary<string, string>? values = null)
    {
        EnsureEnabled();
        var merged = new Dictionary<string, string>(State.Values, StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (var (k, v) in values)
            {
                var field = State.Fields.FirstOrDefault(f => string.Equals(f.Id, k, StringComparison.Ordinal));
                if (field is null) { continue; }
                if (string.Equals(field.Kind, FormFieldKinds.Secret, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Field '{k}' is a secret field; seal it to the vault via SetSecretRef before submitting.");
                }
                merged[k] = v ?? "";
            }
        }

        var secretMap = State.SecretRefs;
        var missing = State.Fields
            .Where(f => f.Required)
            .Where(f => string.Equals(f.Kind, FormFieldKinds.Secret, StringComparison.OrdinalIgnoreCase)
                ? !secretMap.ContainsKey(f.Id)
                : !merged.TryGetValue(f.Id, out var val) || string.IsNullOrWhiteSpace(val))
            .Select(f => f.Label)
            .ToArray();

        if (missing.Length > 0)
        {
            var invalid = State with
            {
                Version = State.Version + 1,
                ValueEntries = ToValueEntries(merged),
                Error = $"Required: {string.Join(", ", missing)}.",
            };
            await Save(invalid, new FormChanged(Id.FullKey(), invalid.Version));
            return invalid;
        }

        var next = State with { Version = State.Version + 1, ValueEntries = ToValueEntries(merged), Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version), new FormSubmitted(Id.FullKey(), next.Version, next.Values, next.SecretRefs));
        return next;
    }

    public async Task<UiFormState> Reset()
    {
        var next = State with
        {
            Version = State.Version + 1,
            ValueEntries = [],
            SecretEntries = [],
            Error = null,
        };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public Task<UiFormState> Read() => Task.FromResult(State);

    private static FormFieldValue[] ToValueEntries(Dictionary<string, string> map)
        => map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new FormFieldValue(kv.Key, kv.Value)).ToArray();

    private static FormSecretEntry[] ToSecretEntries(Dictionary<string, UiSecretRef> map)
        => map.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new FormSecretEntry(kv.Key, kv.Value)).ToArray();

    private void EnsureEnabled()
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"Form '{Id.FullKey()}' is disabled.");
        }
    }

    private FormField RequireField(string fieldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        return State.Fields.FirstOrDefault(f => string.Equals(f.Id, fieldId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown field '{fieldId}' on form '{Id.FullKey()}'.", nameof(fieldId));
    }
}
