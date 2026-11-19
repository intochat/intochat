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
public sealed record SecretRef(
    [property: Id(0)] string VaultKey,
    [property: Id(1)] string Mask = "••••••••")
{
    public SecretRef() : this("", "••••••••") { }
}

[GenerateSerializer, Alias("ui.form.state")]
public sealed record FormState(
    [property: Id(0)] int Version,
    [property: Id(1)] string Title,
    [property: Id(2)] IReadOnlyList<FormField> Fields,
    [property: Id(3)] IReadOnlyDictionary<string, string> Values,
    [property: Id(4)] IReadOnlyDictionary<string, SecretRef> SecretRefs,
    [property: Id(5)] string SubmitLabel,
    [property: Id(6)] bool IsEnabled,
    [property: Id(7)] string? Error)
{
    public FormState() : this(
        0,
        "",
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, SecretRef>(StringComparer.Ordinal),
        "Save",
        true,
        null)
    {
    }
}
