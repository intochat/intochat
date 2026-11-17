using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

[GenerateSerializer, Alias("types.type-validation")]
public sealed class TypeValidationException(
    SemanticFieldKind kind,
    object? rejectedValue,
    IReadOnlyList<string> allowedValues)
    : ArgumentException(BuildMessage(kind, rejectedValue, allowedValues))
{

    [Id(0)] public SemanticFieldKind Kind { get; } = kind;

    [Id(1)] public string[] AllowedValues { get; } = [.. allowedValues];

    [Id(2)] public object? RejectedValue { get; } = kind == SemanticFieldKind.Secret ? null : Display(rejectedValue);

    private static object? Display(object? rejectedValue) => rejectedValue?.ToString();

    private static string BuildMessage(SemanticFieldKind kind, object? rejectedValue, IReadOnlyList<string> allowedValues)
    {
        var shown = kind == SemanticFieldKind.Secret ? "(redacted)" : $"'{rejectedValue}'";
        return $"{shown} is not a valid {kind} value. Allowed {kind} values: {string.Join(", ", allowedValues)}.";
    }
}
