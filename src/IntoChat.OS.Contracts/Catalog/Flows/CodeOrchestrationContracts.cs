using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Flows;

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

/// <summary>Snapshot of a Plane 1 <c>flow.code-orchestration</c> run.</summary>
public sealed record CodeOrchestrationRunView(
    string RunId,
    FlowStatus Status,
    string Title,
    string Goal,
    string[] ScopedContracts,
    string[] PersonOnlyCalls,
    int RepairRoundsUsed,
    CodeOrchestrationResult? Result,
    string? ResultJson,
    string? ExecutionTarget,
    string? ExecutedByMachineId,
    string? RouteReason,
    string? Error,
    string? CrystallizedAppKey = null,
    string? CrystallizedUnitAlias = null);

/// <summary>Result of crystallizing ("Pin as App") a verified Plane 1 orchestration run into a sandboxed unit.</summary>
public sealed record CodeOrchestrationCrystallizeResult(
    bool Succeeded,
    string AppKey,
    string? UnitAlias,
    string[] ScopedContracts,
    string? DeployHash,
    string? ExecutionTarget,
    string? Error);

/// <summary>
/// Plane 1 code orchestration neuron contract (<c>flow.code-orchestration</c>): compiles, Roslyn-AST-sanitizes
/// (fail-closed), and runs C# orchestration scripts inside the least-privilege <c>IntoChat.OS.Sandbox</c> boundary,
/// and optionally crystallizes ("Pin as App") a verified run into a durable sandboxed app via <c>UnitDeployer</c>.
/// </summary>
[NeuronAlias("flow.code-orchestration"), DefaultNeuronType("flow.code-orchestration")]
[Description("Run a Plane 1 C# orchestration script inside IntoChat.OS.Sandbox with fail-closed AST sanitization and least-privilege ScopedContracts, or crystallize a verified run as a pinned app.")]
public interface ICodeOrchestration
{
    const string KeyPattern = "code-orchestration/{new short id}";

    [Description("Start a Plane 1 C# orchestration run. Provide a goal and optional preauthored C# source (`public static class OrchestrationScript { public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct) ... }`), inputJson, and allowedContracts.")]
    Task<CodeOrchestrationRunView> Start(
        string title,
        string goal,
        string? source = null,
        string? inputJson = null,
        string[]? allowedContracts = null);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    [Description("Read the current status, scoped contracts, result.json, and diagnostics of this code orchestration run.")]
    Task<CodeOrchestrationRunView?> Read();

    [Description("Cancel an in-flight or waiting code orchestration run.")]
    Task<CodeOrchestrationRunView?> Cancel(string reason);

    [Description("Pin a verified code orchestration run as a durable sandboxed app via UnitDeployer using a least-privilege ScopedContracts AppToken inside IntoChat.OS.Sandbox (never wildcard */*).")]
    Task<CodeOrchestrationCrystallizeResult> CrystallizeAsync(string appAlias, string? title = null);
}
