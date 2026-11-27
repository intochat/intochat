using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Units;
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

    public string? CrystallizedAppKey { get; set; }

    public string? CrystallizedUnitAlias { get; set; }
}

/// <summary>
/// Plane 1 durable orchestration synapse (<c>flow.code-orchestration</c>):
/// discovers typed Neuron contracts, generates and Roslyn-AST-sanitizes (fail-closed) a standalone <c>net11.0</c> C# script
/// with up to 2 compiler-repair rounds, enforces least-privilege scoped <c>AppToken</c> execution via
/// <see cref="ICodeSandbox"/> (preferring an owner's consented paired client device advertising
/// <see cref="ICodeSandbox.CapabilityId"/> and falling back deterministically to local/cloud
/// <c>IntoChat.OS.Sandbox</c>), extracts structured <c>result.json</c>
/// (<c>status</c>, <c>summary</c>, <c>artifacts</c>, <c>metrics</c>), durably suspends on
/// <see cref="ConfirmationPlan"/> (<c>RequiresPerson = true</c>) whenever a <see cref="PersonOnlyAttribute"/>
/// or confirmation-gated Neuron method requires person approval, and supports <see cref="CrystallizeAsync"/>
/// ("Pin as App") deploying a verified run into <c>UnitDeployer</c> running strictly inside
/// <c>IntoChat.OS.Sandbox</c> with a least-privilege <c>ScopedContracts</c> <c>AppToken</c>.
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed partial class CodeOrchestrationSynapse(
    AppPackageCompiler packageCompiler,
    ICodeOrchestrationAstSanitizer? astSanitizer = null,
    ISandboxRuntimeBridge? sandbox = null,
    ICodeOrchestrationModel? model = null,
    ILogger<CodeOrchestrationSynapse>? logger = null,
    IConnectedComputeRouter? computeRouter = null,
    ISynapseHost? unitDeployer = null) : Flow<CodeOrchestrationInner>, IFlowStarter, ICodeOrchestration
{
    public const string Alias = "flow.code-orchestration";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] StepNames = ["generate", "approve", "execute", "finalize"];

    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(2);

    private readonly ICodeOrchestrationAstSanitizer _astSanitizer = astSanitizer ?? UnavailableCodeOrchestrationAstSanitizer.Instance;

    private readonly CodeOrchestrationCompiler _compiler = new(packageCompiler, astSanitizer ?? UnavailableCodeOrchestrationAstSanitizer.Instance);

    protected override IReadOnlyList<string> Steps => StepNames;

    public async Task<CodeOrchestrationView> RunAsync(
        string title,
        string goal,
        string inputJson = "{}",
        string[]? allowedContracts = null,
        string? preauthoredScript = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(State.Title) || State.Started != default)
        {
            return ToView();
        }

        var effectiveTitle = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(goal) ? "Code orchestration" : goal.Trim())
            : title.Trim();
        var effectiveGoal = string.IsNullOrWhiteSpace(goal) ? effectiveTitle : goal.Trim();
        var owner = DigitalBrain.Access.CallerRules.ActsFor(Context.Caller) ?? "ino";
        var inner = new CodeOrchestrationInner
        {
            Goal = effectiveGoal,
            InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson,
            PreauthoredScript = string.IsNullOrWhiteSpace(preauthoredScript) ? null : preauthoredScript,
            AllowedContracts = allowedContracts ?? [],
        };

        await Start(effectiveTitle, owner, parentFlowId: null, inner).ConfigureAwait(true);
        return ToView();
    }

    public Task<CodeOrchestrationView?> ReadAsync(CancellationToken ct = default)
        => Task.FromResult(State.Started == default && string.IsNullOrWhiteSpace(State.Title)
            ? null
            : ToView());

    public async Task<CodeOrchestrationView?> CancelAsync(string reason, CancellationToken ct = default)
    {
        if (State.Started == default && string.IsNullOrWhiteSpace(State.Title))
        {
            return null;
        }

        if (!State.IsOver)
        {
            await Cancel(string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason).ConfigureAwait(true);
        }

        return ToView();
    }

    /// <summary>
    /// Pins ("Crystallizes") a verified Plane 1 orchestration run as a durable sandboxed app via
    /// <see cref="ISynapseHost"/> (<c>UnitDeployer</c>) running strictly inside the
    /// <c>IntoChat.OS.Sandbox</c> execution boundary with a least-privilege <c>ScopedContracts</c> <c>AppToken</c>.
    /// </summary>
    public async Task<CrystallizedAppReceipt> CrystallizeAsync(
        string alias,
        string? title = null,
        CancellationToken ct = default)
    {
        var normalizedAlias = NormalizeAppAlias(alias);
        var effectiveTitle = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(State.Title) ? normalizedAlias : State.Title)
            : title.Trim();

        CrystallizedAppReceipt Fail(string error, string[]? contracts = null, string? appKey = null, string? deployHash = null, string? target = null)
            => new(
                Alias: normalizedAlias,
                Title: effectiveTitle,
                LiveViewPath: string.IsNullOrWhiteSpace(normalizedAlias) ? string.Empty : $"apps/{normalizedAlias}/view",
                EventTarget: string.IsNullOrWhiteSpace(normalizedAlias) ? string.Empty : new NeuronId(Id.Brain, normalizedAlias, "default").ToString(),
                ScopedContracts: contracts ?? [],
                PlanId: State.Waiting?.ConfirmationPlanId,
                Succeeded: false,
                AppKey: appKey,
                DeployHash: deployHash,
                ExecutionTarget: target,
                Error: error);

        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return Fail("CrystallizeAsync requires a non-empty alphanumeric alias (e.g. 'daily.digest').");
        }

        var inner = State.Inner;
        if (string.IsNullOrWhiteSpace(inner.CompiledScript))
        {
            return Fail("Cannot crystallize: no compiled orchestration script exists on this run.");
        }

        if (State.Status is FlowStatus.Failed or FlowStatus.Cancelled || inner.Result is null || !string.IsNullOrWhiteSpace(inner.Error))
        {
            return Fail($"Cannot crystallize an unverified or failed run (status={State.Status}, error={inner.Error ?? "no result"}).");
        }

        // Fail-closed AST sanitization check before crystallizing.
        if (_astSanitizer is UnavailableCodeOrchestrationAstSanitizer)
        {
            return Fail("AST000: Roslyn AST sanitizer is unavailable; fail-closed policy refuses crystallization.");
        }

        var ast = _astSanitizer.Validate(inner.CompiledScript);
        if (!ast.IsValid)
        {
            return Fail($"AST policy rejected script during crystallization: {string.Join("; ", ast.Diagnostics.Take(5))}");
        }

        // Compute least-privilege ScopedContracts; explicitly forbid wildcards (*/*) and authority contracts.
        var scopedContracts = inner.ScopedContracts.Length > 0
            ? NeuronContractDiscovery.NormalizeScopeList(inner.ScopedContracts)
            : ComputeEffectiveScope(
                inner.AllowedContracts.Length > 0 ? inner.AllowedContracts : null,
                inner.InferredContracts);

        if (scopedContracts.Any(CodeOrchestrationCompiler.IsWildcardContract))
        {
            return Fail("SCOPE002: Cannot crystallize with wildcard ('*' or '*/*') contract grants; least-privilege ScopedContracts are required.", scopedContracts);
        }

        var forbiddenAuthority = scopedContracts.Where(NeuronContractDiscovery.IsAuthorityContract).ToArray();
        if (forbiddenAuthority.Length > 0)
        {
            return Fail($"AUTH001: Cannot crystallize with authority contract(s): {string.Join(", ", forbiddenAuthority)}.", scopedContracts);
        }

        if (sandbox is null)
        {
            return Fail("ISandboxRuntimeBridge is not configured; cannot deploy crystallized app into IntoChat.OS.Sandbox.", scopedContracts);
        }

        var appKey = $"pinned-{normalizedAlias.Replace('.', '-')}";

        // 1. Compile the verified script as a sandbox AppPackage and deploy strictly into IntoChat.OS.Sandbox
        //    with a least-privilege ScopedContracts AppToken (never wildcard */*).
        var package = CodeOrchestrationScriptWrapper.BuildPackage(appKey, effectiveTitle, inner.CompiledScript);
        var compiled = packageCompiler.Compile(package);
        if (!compiled.Succeeded || compiled.EnrichedPackage is null)
        {
            return Fail($"Crystallization sandbox compilation failed: {string.Join("; ", compiled.Errors.Take(5))}", scopedContracts, appKey);
        }

        var sandboxDeploy = await sandbox.DeployScopedPackageAsync(
            Id.Brain,
            compiled.EnrichedPackage,
            compiled.AssemblyBytes,
            stateJson: null,
            settingsJson: null,
            allowedContracts: scopedContracts,
            cancellationToken: ct).ConfigureAwait(true);

        if (!sandboxDeploy.Succeeded)
        {
            return Fail($"IntoChat.OS.Sandbox scoped deployment failed: {sandboxDeploy.Error ?? "unknown error"}", scopedContracts, appKey, target: "IntoChat.OS.Sandbox");
        }

        // 2. Register the pinned app unit through UnitDeployer (ISynapseHost) when available.
        string? deployHash = compiled.EnrichedPackage.Digest;
        string? installPlanId = null;
        if (unitDeployer is not null)
        {
            var unitSource = BuildCrystallizedUnitSource(normalizedAlias, appKey, effectiveTitle, scopedContracts);
            var synapseSource = new SynapseSource(
                normalizedAlias,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unit.cs"] = unitSource,
                },
                Icon: null);

            var deployOutcome = await unitDeployer.Deploy(
                Id.Brain,
                synapseSource,
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
                ct).ConfigureAwait(true);

            if (deployOutcome.Status is DeployStatus.Failed or DeployStatus.Refused)
            {
                await sandbox.UnloadAsync(Id.Brain.Value, appKey, CancellationToken.None).ConfigureAwait(true);
                return Fail(
                    $"UnitDeployer rejected crystallized unit '{normalizedAlias}': {deployOutcome.Reason ?? deployOutcome.Status.ToString()}",
                    scopedContracts,
                    appKey,
                    deployOutcome.Hash,
                    "IntoChat.OS.Sandbox");
            }

            if (!string.IsNullOrWhiteSpace(deployOutcome.Hash))
            {
                deployHash = deployOutcome.Hash;
            }

            installPlanId = deployOutcome.Installs.Values
                .Where(static i => i.Status == InstallStatus.Pending)
                .Select(static i => i.Reason)
                .FirstOrDefault(static p => !string.IsNullOrWhiteSpace(p));
        }

        var liveViewPath = $"apps/{normalizedAlias}/view";
        var eventTarget = new NeuronId(Id.Brain, normalizedAlias, "default").ToString();

        inner.CrystallizedAppKey = appKey;
        inner.CrystallizedUnitAlias = normalizedAlias;
        await Save(
            State with { Inner = inner },
            new FlowStep(
                Id.ToString(),
                "crystallize",
                StepOutcome.Completed,
                $"Crystallized as '{normalizedAlias}' ({appKey}) inside IntoChat.OS.Sandbox.")).ConfigureAwait(true);

        return new CrystallizedAppReceipt(
            Alias: normalizedAlias,
            Title: effectiveTitle,
            LiveViewPath: liveViewPath,
            EventTarget: eventTarget,
            ScopedContracts: scopedContracts,
            PlanId: installPlanId,
            Succeeded: true,
            AppKey: appKey,
            DeployHash: deployHash,
            ExecutionTarget: "IntoChat.OS.Sandbox",
            Error: null);
    }

    private CodeOrchestrationView ToView()
    {
        var inner = State.Inner;
        var liveViewPath = !string.IsNullOrWhiteSpace(inner.CrystallizedUnitAlias)
            ? $"apps/{inner.CrystallizedUnitAlias}/view"
            : null;
        return new CodeOrchestrationView(
            FlowId: Id.ToString(),
            Title: State.Title,
            Status: State.Status,
            CompiledScript: inner.CompiledScript,
            ScopedContracts: inner.ScopedContracts,
            Result: inner.Result,
            Error: inner.Error,
            PlanId: State.Waiting?.ConfirmationPlanId,
            PinnedAppAlias: inner.CrystallizedUnitAlias,
            LiveViewPath: liveViewPath,
            ExecutionTarget: inner.ExecutionTarget,
            ExecutedByMachineId: inner.ExecutedByMachineId);
    }

    internal static string NormalizeAppAlias(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = raw.Trim().ToLowerInvariant();
        cleaned = InvalidAliasChars().Replace(cleaned, ".");
        cleaned = cleaned.Trim('.');
        return cleaned;
    }

    private static string BuildCrystallizedUnitSource(
        string unitAlias,
        string appKey,
        string title,
        string[] scopedContracts)
    {
        var safeTitle = title.Replace("\"", "'", StringComparison.Ordinal);
        var scopeComment = scopedContracts.Length > 0 ? string.Join(", ", scopedContracts) : "none";
        var className = "Pinned" + string.Concat(
            unitAlias.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => char.ToUpperInvariant(s[0]) + s[1..]));
        if (className == "Pinned")
        {
            className = "PinnedOrchestrationUnit";
        }

        return $$"""
            // Crystallized Plane 1 sandboxed app unit.
            // SandboxAppKey: {{appKey}}
            // ScopedContracts: {{scopeComment}}
            // ExecutionBoundary: IntoChat.OS.Sandbox
            using DigitalBrain.Neurons;

            namespace CrystallizedApps;

            public sealed record {{className}}State(
                string AppKey = "{{appKey}}",
                string Title = "{{safeTitle}}",
                string ScopedContracts = "{{scopeComment}}")
            {
                public {{className}}State() : this("{{appKey}}", "{{safeTitle}}", "{{scopeComment}}") { }
            }

            [Neuron("{{unitAlias}}")]
            public sealed class {{className}} : Neuron<{{className}}State>
            {
            }
            """;
    }

    [GeneratedRegex(@"[^a-z0-9.\-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidAliasChars();

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

        var ast = _astSanitizer.Validate(inner.CompiledScript);
        if (!ast.IsValid)
        {
            var detail = string.Join("; ", ast.Diagnostics.Take(5));
            inner.Error = detail;
            throw new FlowFailed(step, $"AST policy rejected script before execution: {detail}");
        }

        if (sandbox is null)
        {
            const string missingSandbox = "ISandboxRuntimeBridge is not configured; cannot execute orchestration script.";
            inner.Error = missingSandbox;
            throw new FlowFailed(step, missingSandbox);
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
