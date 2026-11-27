using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Persisted durable state for <see cref="CodeOrchestrationSynapse"/>.
/// Uses concrete arrays rather than interface/mutable collections to satisfy persisted-state invariants.
/// </summary>
internal sealed class CodeOrchestrationInner
{
    public string Goal { get; set; } = "";

    public string InputJson { get; set; } = "{}";

    public string[] AllowedContracts { get; set; } = [];

    public string? PreauthoredScript { get; set; }

    public string? CompiledScript { get; set; }

    public string[] InferredContracts { get; set; } = [];

    public string[] ScopedContracts { get; set; } = [];

    public string[] PersonOnlyCalls { get; set; } = [];

    public int RepairRoundsUsed { get; set; }

    public bool ApprovalRequired { get; set; }

    public bool RuntimeApprovalRequired { get; set; }

    public string? ApprovalReason { get; set; }

    public bool Approved { get; set; }

    public string? ResultJson { get; set; }

    public CodeOrchestrationResult? Result { get; set; }

    public string? ExecutionTarget { get; set; }

    public string? ExecutedByMachineId { get; set; }

    public string? RouteReason { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Plane 1 durable orchestration synapse (<c>flow.code-orchestration</c>):
/// discovers typed Neuron contracts, generates and Roslyn-AST-sanitizes a standalone <c>net11.0</c> C# script
/// with up to 2 compiler-repair rounds, enforces least-privilege scoped <c>AppToken</c> execution via
/// <see cref="ICodeSandbox"/> (preferring an owner's consented paired client device advertising
/// <see cref="ICodeSandbox.CapabilityId"/> and falling back deterministically to local/cloud
/// <c>IntoChat.OS.Sandbox</c>), extracts structured <c>result.json</c>
/// (<c>status</c>, <c>summary</c>, <c>artifacts</c>, <c>metrics</c>), and durably suspends on
/// <see cref="ConfirmationPlan"/> (<c>RequiresPerson = true</c>) whenever a <see cref="PersonOnlyAttribute"/>
/// or confirmation-gated Neuron method requires person approval.
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed class CodeOrchestrationSynapse(
    AppPackageCompiler packageCompiler,
    ICodeOrchestrationAstSanitizer astSanitizer,
    ISandboxRuntimeBridge sandbox,
    ICodeOrchestrationModel? model = null,
    ILogger<CodeOrchestrationSynapse>? logger = null,
    IConnectedComputeRouter? computeRouter = null) : Flow<CodeOrchestrationInner>, IFlowStarter
{
    public const string Alias = "flow.code-orchestration";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] StepNames = ["generate", "approve", "execute", "finalize"];

    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(2);

    private readonly CodeOrchestrationCompiler _compiler = new(packageCompiler, astSanitizer);

    protected override IReadOnlyList<string> Steps => StepNames;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<CodeOrchestrationInner>(innerJson, Json) ?? new CodeOrchestrationInner());

    public Task<FlowHead> Head()
        => Task.FromResult(new FlowHead(
            [.. StepNames],
            State.Owner,
            State.TraceId,
            State.ParentFlowId,
            State.Started,
            JsonSerializer.Serialize(State.Inner, Json),
            State.Title,
            State.Status,
            [.. State.Done],
            State.Ended,
            null));

    protected override async Task<StepResult<CodeOrchestrationInner>> Run(string step, CodeOrchestrationInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "generate":
                return await RunGenerateAsync(step, inner, ct).ConfigureAwait(true);

            case "approve":
                if (!inner.ApprovalRequired || inner.Approved)
                {
                    return inner.Advance();
                }

                return inner.WaitApproval(step, Context.Time.GetUtcNow().Add(ApprovalWindow));

            case "execute":
                if (inner.ApprovalRequired)
                {
                    inner.Approved = true;
                }

                return await RunExecuteAsync(step, inner, allowRuntimeSuspension: !inner.Approved, ct).ConfigureAwait(true);

            case "finalize":
                if (inner.RuntimeApprovalRequired && inner.Result is null)
                {
                    inner.Approved = true;
                    return await RunExecuteAsync(step, inner, allowRuntimeSuspension: false, ct).ConfigureAwait(true);
                }

                return inner.Advance();

            default:
                throw new FlowFailed(step, $"Unknown step '{step}'.");
        }
    }

    protected override ConfirmationPlan PlanFor(string step, CodeOrchestrationInner inner)
    {
        var contracts = inner.ScopedContracts.Length > 0
            ? string.Join(", ", inner.ScopedContracts)
            : "<none>";
        var gated = inner.PersonOnlyCalls.Length > 0
            ? string.Join(", ", inner.PersonOnlyCalls)
            : "confirmation-gated Neuron call";
        var reason = !string.IsNullOrWhiteSpace(inner.ApprovalReason)
            ? inner.ApprovalReason
            : $"Code orchestration \"{State.Title}\" invokes {gated} with scoped contracts [{contracts}].";

        return new ConfirmationPlan(
            Id: $"orch-{Id.Key}-{step}",
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: $"Approve code orchestration \"{State.Title}\"",
            Body: reason,
            ApproveLabel: "Approve",
            DenyLabel: "Deny",
            TargetLabel: State.Title)
        {
            RequiresPerson = true,
            Kind = "code-orchestration.person-only",
            Subject = Id.ToString(),
        };
    }

    private async Task<StepResult<CodeOrchestrationInner>> RunGenerateAsync(
        string step,
        CodeOrchestrationInner inner,
        CancellationToken ct)
    {
        var goal = !string.IsNullOrWhiteSpace(inner.Goal) ? inner.Goal : State.Title;
        var hasAllowedConstraint = inner.AllowedContracts.Length > 0;
        var allowedFilter = hasAllowedConstraint
            ? SanitizeScopeList(inner.AllowedContracts)
            : null;
        var discoveredContracts = hasAllowedConstraint && allowedFilter!.Length == 0
            ? []
            : NeuronContractDiscovery.Discover(filterAliases: allowedFilter);

        var appKey = CodeOrchestrationScriptWrapper.AppKey(Id.Key);
        var compile = await _compiler.CompileWithRepairAsync(
            brainId: Id.Brain,
            appKey: appKey,
            title: State.Title,
            goal: goal,
            inputJson: string.IsNullOrWhiteSpace(inner.InputJson) ? "{}" : inner.InputJson,
            availableContracts: discoveredContracts,
            allowedContracts: allowedFilter,
            preauthoredScript: inner.PreauthoredScript,
            model: model,
            ct: ct).ConfigureAwait(true);

        inner.RepairRoundsUsed = compile.RepairRoundsUsed;
        inner.CompiledScript = compile.ScriptSource;

        if (!compile.Succeeded)
        {
            var reason = compile.Diagnostics.Length > 0
                ? string.Join("; ", compile.Diagnostics.Take(5))
                : "Failed to compile orchestration script after repair rounds.";
            inner.Error = reason;
            throw new FlowFailed(step, reason);
        }

        inner.InferredContracts = compile.RequiredContracts;
        inner.ScopedContracts = ComputeEffectiveScope(
            hasAllowedConstraint ? inner.AllowedContracts : null,
            compile.RequiredContracts);
        inner.PersonOnlyCalls = compile.PersonOnlyCalls;

        if (compile.PersonOnlyCalls.Length > 0)
        {
            inner.ApprovalRequired = true;
            inner.ApprovalReason =
                $"Script invokes [PersonOnly] / confirmation-gated method(s): {string.Join(", ", compile.PersonOnlyCalls)} "
                + $"under scoped contracts [{string.Join(", ", inner.ScopedContracts)}].";
        }

        return inner.Advance();
    }

    private async Task<StepResult<CodeOrchestrationInner>> RunExecuteAsync(
        string step,
        CodeOrchestrationInner inner,
        bool allowRuntimeSuspension,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inner.CompiledScript))
        {
            throw new FlowFailed(step, "No compiled orchestration script is available to execute.");
        }

        var ast = astSanitizer.Validate(inner.CompiledScript);
        if (!ast.IsValid)
        {
            var detail = string.Join("; ", ast.Diagnostics.Take(5));
            inner.Error = detail;
            throw new FlowFailed(step, $"AST policy rejected script before execution: {detail}");
        }

        var appKey = CodeOrchestrationScriptWrapper.AppKey(Id.Key);
        var package = CodeOrchestrationScriptWrapper.BuildPackage(appKey, State.Title, inner.CompiledScript);
        var compiled = packageCompiler.Compile(package);
        if (!compiled.Succeeded || compiled.EnrichedPackage is null)
        {
            var detail = string.Join("; ", compiled.Errors.Take(5));
            inner.Error = detail;
            throw new FlowFailed(step, $"Orchestration script failed verification before execution: {detail}");
        }

        var candidateContracts = inner.ScopedContracts.Length > 0
            ? inner.ScopedContracts
            : inner.InferredContracts;
        var effectiveScope = ComputeEffectiveScope(
            inner.AllowedContracts.Length > 0 ? inner.AllowedContracts : null,
            candidateContracts);

        var inputArg = string.IsNullOrWhiteSpace(inner.InputJson) ? "{}" : inner.InputJson;
        var payload = JsonSerializer.Serialize(inputArg, Json);

        ComputeRouteDecision? routeDecision = null;
        if (computeRouter is not null)
        {
            routeDecision = await computeRouter.ResolveRouteAsync(Id.Brain, ct).ConfigureAwait(true);
        }

        ICodeSandbox codeSandbox = sandbox;
        var dispatch = await codeSandbox.ExecuteScopedCommandAsync(
            Id.Brain,
            compiled.EnrichedPackage,
            compiled.AssemblyBytes,
            CodeOrchestrationScriptWrapper.Command,
            payload,
            effectiveScope,
            cancellationToken: ct).ConfigureAwait(true);

        inner.ExecutionTarget = dispatch.ExecutionTarget;
        inner.ExecutedByMachineId = dispatch.ExecutedByMachineId;
        inner.RouteReason = DescribeRouteOutcome(routeDecision, dispatch);

        if (!string.IsNullOrWhiteSpace(dispatch.ExecutionTarget))
        {
            (logger ?? OsConsoleLog.For<CodeOrchestrationSynapse>()).LogInformation(
                "Code orchestration '{AppKey}' for brain '{BrainId}' executed via '{ExecutionTarget}' (machine='{MachineId}'): {RouteReason}",
                appKey,
                Id.Brain.Value,
                dispatch.ExecutionTarget,
                dispatch.ExecutedByMachineId ?? "fallback",
                inner.RouteReason);
        }

        if (!string.IsNullOrWhiteSpace(dispatch.Error))
        {
            if (allowRuntimeSuspension && IsApprovalRequirementError(dispatch.Error))
            {
                inner.RuntimeApprovalRequired = true;
                inner.ApprovalReason = dispatch.Error;
                return inner.WaitApproval(step, Context.Time.GetUtcNow().Add(ApprovalWindow));
            }

            inner.Error = dispatch.Error;
            throw new FlowFailed(step, dispatch.Error);
        }

        if (!dispatch.Handled)
        {
            const string unhandled = "Sandbox did not handle the code orchestration command.";
            inner.Error = unhandled;
            throw new FlowFailed(step, unhandled);
        }

        CodeOrchestrationResult parsed;
        try
        {
            parsed = CodeOrchestrationScriptWrapper.ParseResultJson(dispatch.ResultJson);
        }
        catch (Exception parseError) when (parseError is JsonException or FormatException)
        {
            inner.Error = parseError.Message;
            throw new FlowFailed(step, $"Malformed result.json output: {parseError.Message}");
        }

        if (allowRuntimeSuspension
            && string.Equals(parsed.Status, "approval_required", StringComparison.OrdinalIgnoreCase))
        {
            inner.RuntimeApprovalRequired = true;
            inner.ApprovalReason = string.IsNullOrWhiteSpace(parsed.Summary)
                ? "Orchestration script encountered a confirmation-gated Neuron method requiring Person approval."
                : parsed.Summary;
            return inner.WaitApproval(step, Context.Time.GetUtcNow().Add(ApprovalWindow));
        }

        inner.Result = parsed;
        inner.ResultJson = CodeOrchestrationScriptWrapper.SerializeResultJson(parsed);
        inner.Error = null;
        return inner.Advance();
    }

    internal static string[] ComputeEffectiveScope(string[]? allowedContracts, string[] inferredContracts)
    {
        var normalizedInferred = SanitizeScopeList(inferredContracts);
        if (allowedContracts is { Length: > 0 })
        {
            var normalizedAllowed = SanitizeScopeList(allowedContracts);
            if (normalizedAllowed.Length == 0 || normalizedInferred.Length == 0)
            {
                return [];
            }

            // Least-privilege: restrict the minted AppToken strictly to contracts BOTH allowed AND actually referenced.
            return normalizedInferred
                .Where(contract => NeuronContractDiscovery.IsContractAllowed(contract, null, normalizedAllowed))
                .ToArray();
        }

        return normalizedInferred;
    }

    private static string[] SanitizeScopeList(IEnumerable<string> scopes)
        => NeuronContractDiscovery.NormalizeScopeList(scopes)
            .Where(static s => !s.Contains('*', StringComparison.Ordinal) && !NeuronContractDiscovery.IsAuthorityContract(s))
            .ToArray();

    internal static bool IsApprovalRequirementError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("confirmation.person-only", StringComparison.OrdinalIgnoreCase)
            || error.Contains("person-only", StringComparison.OrdinalIgnoreCase)
            || error.Contains("ApprovalRequired", StringComparison.OrdinalIgnoreCase)
            || error.Contains("RequiresPerson", StringComparison.OrdinalIgnoreCase)
            || error.Contains("approval_required", StringComparison.OrdinalIgnoreCase)
            || error.Contains("requires person approval", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DescribeRouteOutcome(ComputeRouteDecision? routeDecision, SandboxDispatchResult dispatch)
    {
        if (string.Equals(dispatch.ExecutionTarget, "connected-client", StringComparison.Ordinal))
        {
            return routeDecision is { Target: ComputeExecutionTarget.ConnectedClient }
                ? routeDecision.Reason
                : $"Executed on consented connected client ({ICodeSandbox.CapabilityId}) '{dispatch.ExecutedByMachineId ?? "unknown"}'.";
        }

        if (string.Equals(dispatch.ExecutionTarget, "aca-dynamic-sessions", StringComparison.Ordinal))
        {
            if (routeDecision is { Target: ComputeExecutionTarget.ConnectedClient, Node: { } plannedClient })
            {
                return $"Connected client '{plannedClient.DeviceName}' ({plannedClient.MachineId}) and local sandbox unavailable; fell back to ACA Dynamic Sessions (Kata MicroVM).";
            }

            return "Executed via headless ACA Dynamic Sessions (Kata MicroVM) fallback.";
        }

        if (routeDecision is { Target: ComputeExecutionTarget.ConnectedClient, Node: { } planned })
        {
            var fallback = string.IsNullOrWhiteSpace(dispatch.ExecutionTarget) ? "sandbox" : dispatch.ExecutionTarget;
            return $"Connected client '{planned.DeviceName}' ({planned.MachineId}) unavailable during dispatch; fell back deterministically to {fallback}.";
        }

        if (routeDecision is not null)
        {
            return routeDecision.Reason;
        }

        return string.IsNullOrWhiteSpace(dispatch.ExecutionTarget)
            ? null
            : $"Executed via IntoChat.OS.Sandbox fallback ({dispatch.ExecutionTarget}).";
    }
}
