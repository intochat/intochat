using IntoChat.OS;
using Orleans;
using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.Form;

// One declarative form per window: a typed field list from the P1.1 catalog, draft values, and a
// single atomic submit. There is no per-field neuron; the whole form is one grain and one blob.
[Alias("form"), Orleans.Metadata.DefaultGrainType(UIVocabulary.FormType)]
public interface IForm : INeuron
{
    Task<FormState> Define(FormDefinition definition);
    Task<FormState> SetDraft(string fieldName, string value);
    Task<FormState> SetSecret(string fieldName, FormSecret reference);
    Task<FormState> Submit(FormSubmission submission);
    [ReadOnly, Alias("read")] Task<FormState> Read();
}

public enum FormFieldKind { PlainText, LongText, Number, Date, DateTime, Boolean, Choice, MultiChoice, Email, Url, Secret, Reference }

// A secret the form holds by reference only; the value lives in the platform ring.
[GenerateSerializer, Alias("ui.form-secret")]
public sealed record FormSecret([property: Id(0)] string Reference, [property: Id(1)] string Label, [property: Id(2)] bool IsSet);

[GenerateSerializer, Alias("ui.form-validation")]
public sealed class FormValidationException(FormFieldKind kind, string? rejectedValue, string[] allowedValues)
    : ArgumentException($"{(kind == FormFieldKind.Secret ? "(redacted)" : $"'{rejectedValue}'")} is not a valid {kind} value. Allowed {kind} values: {string.Join(", ", allowedValues)}.")
{
    [Id(0)] public FormFieldKind Kind { get; } = kind;
    [Id(1)] public string? RejectedValue { get; } = kind == FormFieldKind.Secret ? null : rejectedValue;
    [Id(2)] public string[] AllowedValues { get; } = allowedValues;
}

[GenerateSerializer, Alias("ui.form-field")]
public sealed record FormField(
    [property: Id(0)] string Name,
    [property: Id(1)] string Label,
    [property: Id(2)] FormFieldKind Kind,
    [property: Id(3)] bool Required = false,
    [property: Id(4)] IReadOnlyList<string>? Choices = null);

[GenerateSerializer, Alias("ui.form-definition")]
public sealed record FormDefinition(
    [property: Id(0)] string Title,
    [property: Id(1)] IReadOnlyList<FormField> Fields);

[GenerateSerializer, Alias("ui.form-field-value")]
public sealed record FormFieldValue(
    [property: Id(0)] string Name,
    [property: Id(1)] string? Value = null,
    [property: Id(2)] FormSecret? Secret = null);

[GenerateSerializer, Alias("ui.form-submission")]
public sealed record FormSubmission(
    [property: Id(0)] IReadOnlyList<FormFieldValue> Values,
    [property: Id(1)] int ExpectedRevision = 0);

[GenerateSerializer, Alias("ui.form-field-state")]
public sealed class FormFieldState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string Label { get; set; } = "";
    [Id(2)] public FormFieldKind Kind { get; set; }
    [Id(3)] public bool Required { get; set; }
    [Id(4)] public List<string> Choices { get; set; } = [];
    [Id(5)] public string? Value { get; set; }
    [Id(6)] public bool SecretSet { get; set; }
    [Id(7)] public bool Supported { get; set; }
    [Id(8)] public FormSecret? Secret { get; set; }
}

[GenerateSerializer, Alias("ui.form-state")]
[ExportableState]
public sealed class FormState
{
    [Id(0)] public string FormId { get; set; } = "";
    [Id(1)] public string Title { get; set; } = "";
    [Id(2)] public int Revision { get; set; }
    [Id(3)] public List<FormFieldState> Fields { get; set; } = [];
    [Id(4)] public bool Submitted { get; set; }
}

// Kinds without a dedicated input render as plain text; the fallback is declared, never a compile
// or deploy. The catalog still tells the assistant the real kind and sensitivity.
public static class FormFieldSupport
{
    public const string FallbackExplanation = "This field kind has no dedicated input yet and is shown as plain text.";

    public static bool Supports(FormFieldKind kind) => kind != FormFieldKind.Reference;
}
