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
public sealed record CodeOrchestrationView(
    string FlowId,
    string Title,
    FlowStatus Status,
    string? CompiledScript,
    string[] ScopedContracts,
    CodeOrchestrationResult? Result,
    string? Error,
    string? PlanId = null,
    string? PinnedAppAlias = null,
    string? LiveViewPath = null,
    string? ExecutionTarget = null,
    string? ExecutedByMachineId = null);

/// <summary>Receipt returned when crystallizing ("Pin as App") a verified Plane 1 orchestration run into a sandboxed app.</summary>
public sealed record CrystallizedAppReceipt(
    string Alias,
    string Title,
    string LiveViewPath,
    string EventTarget,
    string[] ScopedContracts,
    string? PlanId = null,
    bool Succeeded = true,
    string? AppKey = null,
    string? DeployHash = null,
    string? ExecutionTarget = null,
    string? Error = null);

/// <summary>
/// Plane 1 code orchestration neuron contract (<c>code.orchestration</c> / <c>flow.code-orchestration</c>):
/// compiles, Roslyn-AST-sanitizes (fail-closed), and runs C# orchestration scripts inside the least-privilege
/// <c>IntoChat.OS.Sandbox</c> boundary, and crystallizes ("Pin as App") a verified run into a durable sandboxed
/// app via <c>UnitDeployer</c>.
/// </summary>
[NeuronAlias("code.orchestration"), DefaultNeuronType("flow.code-orchestration")]
[Description("Run a Plane 1 C# orchestration script inside IntoChat.OS.Sandbox with fail-closed AST sanitization and least-privilege ScopedContracts, or crystallize a verified run as a pinned app.")]
public interface ICodeOrchestration
{
    const string KeyPattern = "code-orchestration/{new short id}";

    [Description("Run a Plane 1 C# orchestration script. Provide title, goal, optional inputJson, allowedContracts, and preauthoredScript (`public static class OrchestrationScript { public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct) ... }`).")]
    Task<CodeOrchestrationView> RunAsync(
        string title,
        string goal,
        string inputJson = "{}",
        string[]? allowedContracts = null,
        string? preauthoredScript = null,
        CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    [Description("Read the current status, scoped contracts, result.json, and diagnostics of this code orchestration run.")]
    Task<CodeOrchestrationView?> ReadAsync(CancellationToken ct = default);

    [Description("Pin a verified code orchestration run as a durable sandboxed app via UnitDeployer using a least-privilege ScopedContracts AppToken inside IntoChat.OS.Sandbox (never wildcard */*).")]
    Task<CrystallizedAppReceipt> CrystallizeAsync(
        string alias,
        string? title = null,
        CancellationToken ct = default);

    [Description("Cancel an in-flight or waiting code orchestration run.")]
    Task<CodeOrchestrationView?> CancelAsync(string reason, CancellationToken ct = default);
}
