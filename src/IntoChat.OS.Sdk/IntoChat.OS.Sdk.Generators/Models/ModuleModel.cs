using IntoChat.OS.Sdk.Generators.Models;

namespace IntoChat.OS.Sdk.Generators;

// Value-only pipeline models: strings, bools and EquatableArray. Symbols never cross the pipeline.
internal sealed record FieldModel(string Name, bool Optional) : IEquatable<FieldModel>;

internal sealed record IntegrationModel(
    string Id,
    string DisplayName,
    EquatableArray<FieldModel> Secrets,
    EquatableArray<FieldModel> Settings) : IEquatable<IntegrationModel>;

internal sealed record DiagnosticModel(string Id, string Argument, string FilePath, int Start, int Length, int Line, int Character)
    : IEquatable<DiagnosticModel>;

internal sealed record ModuleModel(
    string? Namespace,
    string ClassName,
    string Id,
    string? ContractsAnchor,
    string? LicenseProduct,
    EquatableArray<IntegrationModel> Integrations,
    EquatableArray<DiagnosticModel> Diagnostics) : IEquatable<ModuleModel>;

internal sealed record HostingModel(
    string? Namespace,
    string ClassName,
    string ModuleType,
    EquatableArray<DiagnosticModel> Diagnostics) : IEquatable<HostingModel>;
