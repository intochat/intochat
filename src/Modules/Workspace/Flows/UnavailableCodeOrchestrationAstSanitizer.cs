using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Fail-closed fallback <see cref="ICodeOrchestrationAstSanitizer"/> used when the Roslyn module is not registered in-process.
/// </summary>
internal sealed class UnavailableCodeOrchestrationAstSanitizer : ICodeOrchestrationAstSanitizer
{
    public static UnavailableCodeOrchestrationAstSanitizer Instance { get; } = new();

    private UnavailableCodeOrchestrationAstSanitizer()
    {
    }

    public AstSanitizationResult Validate(string source)
        => new(
            IsValid: false,
            Violations:
            [
                new AstViolation(
                    "AST000",
                    "Roslyn AST sanitizer is not registered in-process; orchestration script validation cannot proceed.",
                    1,
                    string.Empty),
            ]);

    public AstContractUsage InspectContractUsage(string source, IReadOnlyList<AstContractDescriptor> catalog)
        => new([], []);
}
