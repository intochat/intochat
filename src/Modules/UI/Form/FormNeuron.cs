using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Form.Signals;

namespace IntoChat.OS.Modules.UI.Form;

[Neuron("ui.form")]
internal sealed class FormNeuron : Neuron<FormState>, IForm
{
    public async Task<FormState> Configure(
        string title,
        IReadOnlyList<FormField> fields,
        IReadOnlyDictionary<string, string>? initialValues = null,
        string submitLabel = "Save",
        bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(fields);
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
            else if (State.Values.TryGetValue(field.Id, out var existing))
            {
                values[field.Id] = existing;
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            Title = title ?? "",
            Fields = [.. fields],
            Values = values,
            SubmitLabel = string.IsNullOrWhiteSpace(submitLabel) ? "Save" : submitLabel,
            IsEnabled = isEnabled,
            Error = null,
        };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<FormState> SetValue(string fieldId, string value)
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
        var next = State with { Version = State.Version + 1, Values = values, Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<FormState> SetSecretRef(string fieldId, SecretRef secretRef)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef.VaultKey);
        var field = RequireField(fieldId);
        var refs = new Dictionary<string, SecretRef>(State.SecretRefs, StringComparer.Ordinal)
        {
            [field.Id] = secretRef,
        };
        var next = State with { Version = State.Version + 1, SecretRefs = refs, Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public async Task<FormState> Submit(IReadOnlyDictionary<string, string>? values = null)
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

        var missing = State.Fields
            .Where(f => f.Required)
            .Where(f => string.Equals(f.Kind, FormFieldKinds.Secret, StringComparison.OrdinalIgnoreCase)
                ? !State.SecretRefs.ContainsKey(f.Id)
                : !merged.TryGetValue(f.Id, out var val) || string.IsNullOrWhiteSpace(val))
            .Select(f => f.Label)
            .ToArray();

        if (missing.Length > 0)
        {
            var invalid = State with
            {
                Version = State.Version + 1,
                Values = merged,
                Error = $"Required: {string.Join(", ", missing)}.",
            };
            await Save(invalid, new FormChanged(Id.FullKey(), invalid.Version));
            return invalid;
        }

        var next = State with { Version = State.Version + 1, Values = merged, Error = null };
        await Save(next, new FormChanged(Id.FullKey(), next.Version), new FormSubmitted(Id.FullKey(), next.Version, next.Values, next.SecretRefs));
        return next;
    }

    public async Task<FormState> Reset()
    {
        var next = State with
        {
            Version = State.Version + 1,
            Values = new Dictionary<string, string>(StringComparer.Ordinal),
            SecretRefs = new Dictionary<string, SecretRef>(StringComparer.Ordinal),
            Error = null,
        };
        await Save(next, new FormChanged(Id.FullKey(), next.Version));
        return next;
    }

    public Task<FormState> Read() => Task.FromResult(State);

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
