using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>An artifact emitted inside <c>result.json</c> by a Plane 1 code orchestration script.</summary>
public sealed record CodeOrchestrationArtifact(
    string Name,
    string Kind,
    string? Content = null,
    string? Uri = null);

/// <summary>A single numeric metric emitted inside <c>result.json</c> by a Plane 1 code orchestration script.</summary>
public sealed record CodeOrchestrationMetric(
    string Name,
    double Value,
    string? Unit = null);

/// <summary>Structured <c>result.json</c> contract produced by Plane 1 code orchestration.</summary>
public sealed record CodeOrchestrationResult(
    string Status,
    string Summary,
    CodeOrchestrationArtifact[] Artifacts,
    CodeOrchestrationMetric[] Metrics);

/// <summary>Prompt context sent to an <see cref="ICodeOrchestrationModel"/> for initial generation or compiler repair.</summary>
public sealed record CodeOrchestrationPrompt(
    BrainId BrainId,
    string Goal,
    string InputJson,
    string ContractCatalogPrompt,
    string[] AvailableContractAliases,
    string? PreviousScript,
    string[] PreviousDiagnostics,
    int RepairRound);

/// <summary>Abstraction for generating and repairing Plane 1 C# orchestration scripts.</summary>
public interface ICodeOrchestrationModel
{
    Task<string> GenerateScriptAsync(CodeOrchestrationPrompt prompt, CancellationToken ct = default);
}
