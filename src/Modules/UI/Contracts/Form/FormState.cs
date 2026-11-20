namespace IntoChat.OS.Modules.UI.Form;

public static class FormFieldKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Multiline = "multiline";
    public const string Secret = "secret";
}

[GenerateSerializer, Alias("ui.form.field")]
public sealed record FormField(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string Kind = FormFieldKinds.Text,
    [property: Id(3)] string Placeholder = "",
    [property: Id(4)] bool Required = false,
    [property: Id(5)] int? MaxLength = null)
{
    public FormField() : this("", "", FormFieldKinds.Text, "", false, null) { }
}

/// <summary>
/// Opaque vault reference stored in place of plaintext for <see cref="FormFieldKinds.Secret"/> fields.
/// Plaintext secret values never enter neuron state or journals.
/// </summary>
[GenerateSerializer, Alias("ui.form.secret-ref")]
public sealed record UiSecretRef(
    [property: Id(0)] string VaultKey,
    [property: Id(1)] string Mask = "••••••••")
{
    public UiSecretRef() : this("", "••••••••") { }
}

[GenerateSerializer, Alias("ui.form.value")]
public sealed record FormFieldValue(
    [property: Id(0)] string Key,
    [property: Id(1)] string Value)
{
    public FormFieldValue() : this("", "") { }
}

[GenerateSerializer, Alias("ui.form.secret-entry")]
public sealed record FormSecretEntry(
    [property: Id(0)] string Key,
    [property: Id(1)] UiSecretRef SecretRef)
{
    public FormSecretEntry() : this("", new UiSecretRef()) { }
}

[GenerateSerializer, Alias("ui.form.state")]
public sealed record UiFormState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] FormField[] Fields,
    [property: Id(3)] FormFieldValue[] ValueEntries,
    [property: Id(4)] FormSecretEntry[] SecretEntries,
    [property: Id(5)] string SubmitLabel,
    [property: Id(6)] bool IsEnabled,
    [property: Id(7)] string? Error)
{
    public UiFormState() : this(
        0,
        "",
        [],
        [],
        [],
        "Save",
        true,
        null)
    {
    }

    public IReadOnlyDictionary<string, string> Values
        => ValueEntries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, UiSecretRef> SecretRefs
        => SecretEntries.ToDictionary(e => e.Key, e => e.SecretRef, StringComparer.Ordinal);
}
