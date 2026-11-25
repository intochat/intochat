namespace IntoChat.OS.Modules.Microsoft.Roslyn;

/// <summary>A single Roslyn AST policy violation discovered before compiling an orchestration script.</summary>
public sealed record AstViolation(string RuleId, string Message, int Line, string Snippet)
{
    /// <summary>Formats the violation as a compiler-style diagnostic suitable for model repair feedback.</summary>
    public string ToDiagnosticString() => $"Script.cs({Line}): {RuleId}: {Message}";
}

/// <summary>Result of validating a Plane 1 orchestration script's Roslyn syntax tree.</summary>
public sealed record AstSanitizationResult(bool IsValid, AstViolation[] Violations)
{
    public string[] Diagnostics => Violations.Select(static v => v.ToDiagnosticString()).ToArray();
}

/// <summary>Lightweight metadata for one discovered Neuron contract used during static AST inspection.</summary>
public sealed record AstContractDescriptor(
    string Alias,
    string FullName,
    string ShortName,
    string[] AllAliases,
    string[] PersonOnlyMethods);

/// <summary>Static AST summary of contracts and confirmation-gated methods referenced by a script.</summary>
public sealed record AstContractUsage(string[] ReferencedContracts, string[] PersonOnlyCalls);

/// <summary>
/// Roslyn AST sanitizer and contract-usage inspector for Plane 1 code orchestration scripts.
/// Implemented in <c>IntoChat.OS.Modules.Roslyn</c> so Roslyn remains isolated to the Roslyn module.
/// </summary>
public interface ICodeOrchestrationAstSanitizer
{
    /// <summary>Validates <paramref name="source"/> using Roslyn's C# syntax tree.</summary>
    AstSanitizationResult Validate(string source);

    /// <summary>
    /// Inspects the Roslyn syntax tree to discover which Neuron contracts and <c>[PersonOnly]</c>
    /// methods are referenced through <c>ISandboxBrain</c> (<c>os.Get&lt;T&gt;</c>, <c>os.Notifications</c>, etc.).
    /// </summary>
    AstContractUsage InspectContractUsage(string source, IReadOnlyList<AstContractDescriptor> catalog);
}
