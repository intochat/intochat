using System.Text.Json;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices.Link;

[GenerateSerializer, Alias("devices.pc-resource.pending-console-plan")]
internal sealed record PendingConsolePlan(
    [property: Id(0)] string PlanId,
    [property: Id(1)] string MachineId,
    [property: Id(2)] string Command,
    [property: Id(3)] string? RootLabel,
    [property: Id(4)] bool Consumed,
    [property: Id(5)] DateTimeOffset CreatedAtUtc);

[GenerateSerializer, Alias("devices.pc-resource.state")]
internal sealed record RemotePcResourceState(
    [property: Id(0)] PendingConsolePlan[] PendingPlans)
{
    public RemotePcResourceState() : this([]) { }
}

/// <summary>
/// Cloud-brain proxy neuron (<c>devices.pc-resource</c>) exposing Mode B connected Windows PC resources
/// (consented folders and <c>[PersonOnly]</c>-gated confined shell) to cloud agents and orchestration flows.
/// </summary>
[Neuron("devices.pc-resource")]
internal sealed class RemotePcResourceNeuron(IServiceProvider services) : Neuron<RemotePcResourceState>, IRemotePcResource
{
    private const int MaxStoredPlans = 64;

    private IConnectedComputeRouter Router => services.GetRequiredService<IConnectedComputeRouter>();

    private IConfirmationRequests Confirmations() => Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    public async Task<ConnectedPcResourceNode[]> ListConnectedPcsAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await Router.ListNodesAsync(Id.Brain, cancellationToken).ConfigureAwait(true);
        return nodes
            .Select(static n => new ConnectedPcResourceNode(
                MachineId: n.MachineId,
                DeviceName: n.DeviceName,
                Online: n.Status == ComputeNodeStatus.Online,
                AllowsFolders: n.IsEligibleForWindowsFolderGateway,
                AllowsConsole: n.IsEligibleForConsole,
                AllowsCodeSandbox: n.IsEligibleForSovereignCompute,
                ConsentedFolders: n.ConsentedFolderLabels ?? []))
            .ToArray();
    }

    public async Task<ConnectedResourceEntry[]> ListFolderAsync(
        string rootLabel,
        string relativePath = "",
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchFolderAsync(
            "list",
            new FolderResourcePayload(RootLabel: rootLabel, RelativePath: relativePath),
            machineId,
            cancellationToken).ConfigureAwait(true);

        return result.Entries ?? [];
    }

    public async Task<string> ReadFileTextAsync(
        string rootLabel,
        string relativePath,
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchFolderAsync(
            "read-text",
            new FolderResourcePayload(RootLabel: rootLabel, RelativePath: relativePath),
            machineId,
            cancellationToken).ConfigureAwait(true);

        return result.TextContent ?? string.Empty;
    }

    public async Task WriteFileTextAsync(
        string rootLabel,
        string relativePath,
        string content,
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = await DispatchFolderAsync(
            "write-text",
            new FolderResourcePayload(RootLabel: rootLabel, RelativePath: relativePath, Content: content),
            machineId,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<ConnectedResourceEntry[]> SearchFolderAsync(
        string rootLabel,
        string pattern,
        string relativePath = "",
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchFolderAsync(
            "search",
            new FolderResourcePayload(RootLabel: rootLabel, RelativePath: relativePath, SearchPattern: pattern),
            machineId,
            cancellationToken).ConfigureAwait(true);

        return result.Entries ?? [];
    }

    public async Task<ConnectedResourceEntry?> StatPathAsync(
        string rootLabel,
        string relativePath,
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchFolderAsync(
            "stat",
            new FolderResourcePayload(RootLabel: rootLabel, RelativePath: relativePath),
            machineId,
            cancellationToken).ConfigureAwait(true);

        return result.Exists == true ? result.Entries?.FirstOrDefault() : null;
    }

    public async Task<RemotePcShellOutcome> RunConsoleCommandAsync(
        string command,
        string? rootLabel = null,
        int timeoutSeconds = 60,
        string? machineId = null,
        string? approvedPlanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var nodes = await Router.ListNodesAsync(Id.Brain, cancellationToken).ConfigureAwait(true);
        var target = nodes.FirstOrDefault(n =>
            n.IsEligibleForConsole
            && (string.IsNullOrWhiteSpace(machineId) || string.Equals(n.MachineId, machineId.Trim(), StringComparison.Ordinal)));
        if (target is null)
        {
            return new RemotePcShellOutcome(
                Status: "unavailable",
                Error: "No online connected PC with [PersonOnly] IConsole consent is available.");
        }

        var normalizedCommand = command.Trim();
        var normalizedRootLabel = string.IsNullOrWhiteSpace(rootLabel) ? null : rootLabel.Trim();

        // Durable [PersonOnly] human approval gate BEFORE dispatching any shell command to the PC.
        if (string.IsNullOrWhiteSpace(approvedPlanId))
        {
            var planId = $"pc-console-{Guid.NewGuid():N}"[..24];
            var subject = BuildApprovalSubject(target.MachineId, normalizedCommand, normalizedRootLabel);
            await Confirmations().Request(new ConfirmationPlan(
                Id: planId,
                Action: ConfirmationAction.Other,
                Destructive: true,
                Title: $"Run shell command on {target.DeviceName}?",
                Body: $"Command: {normalizedCommand}" + (normalizedRootLabel is null ? "" : $" (folder: {normalizedRootLabel})"),
                ApproveLabel: "Approve & run",
                DenyLabel: "Deny",
                TargetLabel: target.DeviceName)
            {
                RequiresPerson = true,
                Kind = "pc.console.person-only",
                Subject = subject,
            }).ConfigureAwait(true);

            var existing = State.PendingPlans ?? [];
            var retained = existing.Length >= MaxStoredPlans
                ? existing[^(MaxStoredPlans - 1)..]
                : existing;
            var created = new PendingConsolePlan(
                PlanId: planId,
                MachineId: target.MachineId,
                Command: normalizedCommand,
                RootLabel: normalizedRootLabel,
                Consumed: false,
                CreatedAtUtc: Context.Time.GetUtcNow());
            await Store(State with { PendingPlans = [.. retained, created] }).ConfigureAwait(true);

            return new RemotePcShellOutcome(
                Status: "approval_required",
                PlanId: planId,
                ExecutedByMachineId: target.MachineId);
        }

        var planIdTrimmed = approvedPlanId.Trim();
        var plans = State.PendingPlans ?? [];
        var planIndex = Array.FindIndex(plans, p => string.Equals(p.PlanId, planIdTrimmed, StringComparison.Ordinal));
        if (planIndex < 0)
        {
            return new RemotePcShellOutcome(
                Status: "denied",
                PlanId: planIdTrimmed,
                Error: "Approval plan was not issued by this brain for a PC console command.",
                ExecutedByMachineId: target.MachineId);
        }

        var plan = plans[planIndex];
        if (plan.Consumed)
        {
            return new RemotePcShellOutcome(
                Status: "denied",
                PlanId: planIdTrimmed,
                Error: "Approval plan has already been consumed; request a fresh [PersonOnly] approval.",
                ExecutedByMachineId: target.MachineId);
        }

        if (!string.Equals(plan.MachineId, target.MachineId, StringComparison.Ordinal)
            || !string.Equals(plan.Command, normalizedCommand, StringComparison.Ordinal)
            || !string.Equals(plan.RootLabel, normalizedRootLabel, StringComparison.OrdinalIgnoreCase))
        {
            return new RemotePcShellOutcome(
                Status: "denied",
                PlanId: planIdTrimmed,
                Error: "Approval plan does not match the requested machine, command, or folder root.",
                ExecutedByMachineId: target.MachineId);
        }

        var outcome = await Confirmations().Outcome(planIdTrimmed).ConfigureAwait(true);
        if (outcome is null)
        {
            return new RemotePcShellOutcome(
                Status: "approval_required",
                PlanId: planIdTrimmed,
                Error: "Waiting for [PersonOnly] approval.",
                ExecutedByMachineId: target.MachineId);
        }

        if (outcome.Outcome != ConfirmationOutcome.Approved)
        {
            var updatedDenied = (PendingConsolePlan[])plans.Clone();
            updatedDenied[planIndex] = plan with { Consumed = true };
            await Store(State with { PendingPlans = updatedDenied }).ConfigureAwait(true);

            return new RemotePcShellOutcome(
                Status: "denied",
                PlanId: planIdTrimmed,
                Error: "The person denied running this shell command on the PC.",
                ExecutedByMachineId: target.MachineId);
        }

        // Strict single-use: mark consumed and persist BEFORE dispatching to the PC.
        var updatedConsumed = (PendingConsolePlan[])plans.Clone();
        updatedConsumed[planIndex] = plan with { Consumed = true };
        await Store(State with { PendingPlans = updatedConsumed }).ConfigureAwait(true);

        var proof = LocalPcResourceExecutor.CreateApprovalProof(
            planIdTrimmed,
            Id.Brain.Value,
            target.MachineId,
            normalizedCommand,
            normalizedRootLabel);
        var payloadJson = JsonSerializer.Serialize(
            new ConsoleResourcePayload(
                Command: normalizedCommand,
                RootLabel: normalizedRootLabel,
                ApprovalPlanId: planIdTrimmed),
            DeviceWireJson.Default.ConsoleResourcePayload);

        var response = await Router.TryExecuteResourceOnConnectedClientAsync(
            Id.Brain,
            new PcResourceRequest(
                JobId: NewJobId(),
                BrainId: Id.Brain.Value,
                Capability: ClientComputeConsentPolicy.ConsoleCapability,
                Operation: "run",
                PayloadJson: payloadJson,
                ApprovalToken: proof,
                TimeoutSeconds: timeoutSeconds),
            preferredMachineId: target.MachineId,
            ct: cancellationToken).ConfigureAwait(true);

        if (response is null)
        {
            return new RemotePcShellOutcome(
                Status: "unavailable",
                PlanId: planIdTrimmed,
                Error: $"Connected PC '{target.DeviceName}' went offline before executing the command.",
                ExecutedByMachineId: target.MachineId);
        }

        var consoleResult = string.IsNullOrWhiteSpace(response.ResultJson)
            ? null
            : JsonSerializer.Deserialize(response.ResultJson, DeviceWireJson.Default.ConsoleResourceResult);

        if (!response.Ok || !string.IsNullOrWhiteSpace(response.Error))
        {
            return new RemotePcShellOutcome(
                Status: "failed",
                PlanId: planIdTrimmed,
                ExitCode: consoleResult?.ExitCode,
                StandardOutput: consoleResult?.StandardOutput,
                StandardError: consoleResult?.StandardError,
                Error: response.Error ?? "Remote PC shell execution failed.",
                ExecutedByMachineId: response.ExecutedByMachineId ?? target.MachineId);
        }

        return new RemotePcShellOutcome(
            Status: "completed",
            PlanId: planIdTrimmed,
            ExitCode: consoleResult?.ExitCode ?? 0,
            StandardOutput: consoleResult?.StandardOutput ?? string.Empty,
            StandardError: consoleResult?.StandardError ?? string.Empty,
            ExecutedByMachineId: response.ExecutedByMachineId ?? target.MachineId);
    }

    private async Task<FolderResourceResult> DispatchFolderAsync(
        string operation,
        FolderResourcePayload payload,
        string? machineId,
        CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(payload, DeviceWireJson.Default.FolderResourcePayload);
        var response = await Router.TryExecuteResourceOnConnectedClientAsync(
            Id.Brain,
            new PcResourceRequest(
                JobId: NewJobId(),
                BrainId: Id.Brain.Value,
                Capability: ClientComputeConsentPolicy.WindowsFolderGatewayCapability,
                Operation: operation,
                PayloadJson: payloadJson),
            preferredMachineId: machineId,
            ct: ct).ConfigureAwait(true);

        if (response is null)
        {
            throw new InvalidOperationException("No online connected PC with consented folder access (IWindowsFolderGateway) is available.");
        }

        if (!response.Ok || !string.IsNullOrWhiteSpace(response.Error))
        {
            throw new UnauthorizedAccessException(response.Error ?? "Connected PC refused the folder operation.");
        }

        if (string.IsNullOrWhiteSpace(response.ResultJson))
        {
            return new FolderResourceResult();
        }

        return JsonSerializer.Deserialize(response.ResultJson, DeviceWireJson.Default.FolderResourceResult)
            ?? new FolderResourceResult();
    }

    private static string BuildApprovalSubject(string machineId, string command, string? rootLabel)
        => string.IsNullOrWhiteSpace(rootLabel)
            ? $"{machineId}:{command}"
            : $"{machineId}:{rootLabel}:{command}";

    private static string NewJobId() => "res-" + Guid.NewGuid().ToString("N")[..12];
}
