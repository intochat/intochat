using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DigitalBrain.Access;
using DigitalBrain.Confirmation;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.State;
using IntoChat.Aspire.Bridge;
using IntoChat.Aspire.Domain;
using Microsoft.Extensions.Options;

namespace IntoChat.Aspire;

internal sealed record AspirePendingCommand(
    string PlanId,
    string Resource,
    string InstanceId,
    string Command,
    DateTimeOffset RequestedAt = default);

[ExportableState]
internal sealed record AspireTelemetryState(
    AspireLogEntry[] StructuredLogs,
    AspireTraceSpan[] Traces)
{
    public AspireTelemetryState() : this([], [])
    {
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AspirePendingCommand[]? PendingCommands { get; init; }
}

internal static partial class AspireLiveTelemetry
{
    private const int MaxLiveSpans = 200;
    private static readonly ConcurrentQueue<AspireTraceSpan> LiveSpans = new();
    private static int _listenerInitialized;

    public static void EnsureListener()
    {
        if (Interlocked.Exchange(ref _listenerInitialized, 1) != 0)
        {
            return;
        }

        var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.StartsWith("IntoChat", StringComparison.OrdinalIgnoreCase)
                || source.Name.StartsWith("DigitalBrain", StringComparison.OrdinalIgnoreCase)
                || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase),
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = static activity =>
            {
                var status = activity.Status == ActivityStatusCode.Error ? "Error" : "Ok";
                var error = activity.Status == ActivityStatusCode.Error
                    ? Sanitize(activity.StatusDescription ?? activity.GetTagItem("error.type")?.ToString())
                    : null;
                var resource = string.IsNullOrWhiteSpace(activity.Source.Name) ? KernelNames.Os : activity.Source.Name;
                LiveSpans.Enqueue(new AspireTraceSpan(
                    activity.TraceId.ToString(),
                    activity.SpanId.ToString(),
                    resource,
                    activity.DisplayName ?? activity.OperationName,
                    status,
                    (long)Math.Round(activity.Duration.TotalMilliseconds),
                    error));

                while (LiveSpans.Count > MaxLiveSpans && LiveSpans.TryDequeue(out _))
                {
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public static AspireTraceSpan[] SnapshotSpans() => [.. LiveSpans];

    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        var step1 = UrlCredsRegex().Replace(value, "$1[REDACTED]@");
        var step2 = TokenRegex().Replace(step1, "[REDACTED]");
        return KvSecretRegex().Replace(step2, "$1$2[REDACTED]");
    }

    [GeneratedRegex(@"(https?://)[^/\s:@""']+:[^/\s@""']+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredsRegex();

    [GeneratedRegex(@"(?:Bearer\s+[A-Za-z0-9._\-/+]+=*|sk-(?:proj-|ant-(?:api\d{2}-)?)?[A-Za-z0-9_\-]{8,}|github_pat_[A-Za-z0-9_]{12,}|gh[pousr]_[A-Za-z0-9_]{8,}|AIza[0-9A-Za-z_\-]{20,}|xox[baprs]-[0-9A-Za-z\-]{10,}|vault://[A-Za-z0-9._\-/:]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?i)\b(password|pwd|client_secret|access_token|refresh_token|api_key|apikey)\s*([=:])\s*([^\s;,""'\}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex KvSecretRegex();
}

[Neuron("aspire", State = "aspire")]
internal sealed partial class AspireNeuron : Neuron<AspireTelemetryState>, IAspire, IAspireFeed
{
    internal const string CommandKind = "aspire.command";
    private const int MaxPending = 16;
    private static readonly TimeSpan ConsoleLogTimeout = TimeSpan.FromSeconds(10);

    [DigitalBrain.Slot(Key = IConfirmations.ConfirmationsNeuronKey, Required = false)]
    private partial IConfirmationRequests? Confirmations { get; }

    private readonly AspireHostStore? _store;
    private readonly IAspireRuntimeBridge? _bridge;
    private readonly string? _self;
    private readonly AspireOptions _options;

    public AspireNeuron(
        AspireHostStore? store = null,
        IAspireRuntimeBridge? bridge = null,
        AspireInstance? self = null,
        IOptions<AspireOptions>? options = null)
    {
        _store = store;
        _bridge = bridge;
        _self = self?.Name;
        _options = options?.Value ?? new AspireOptions();
        AspireLiveTelemetry.EnsureListener();
    }

    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        _store?.Register(Id.Brain);
        return Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));
    }

    protected override Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        _store?.Unregister(Id.Brain);
        return Task.CompletedTask;
    }

    public Task<AspireResources> Resources()
        => Task.FromResult(_store?.Read() ?? new AspireResources(false, []));

    public async Task<string[]> ConsoleLog(string resource, int tail = 50)
    {
        if (_store is null || _bridge is null)
        {
            return [];
        }

        var topology = _store.Snapshot();
        if (!topology.Connected || topology.Resolve(resource) is not ResolvedTarget resolved)
        {
            return [];
        }

        return await _bridge.ConsoleLogAsync(resolved.Instance.Id, tail, ConsoleLogTimeout, CancellationToken.None);
    }

    public async Task Changed(AspireResourceChanged[] changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (Context.Caller is not { Actor: Actor.Module { Id: AspireModule.ManifestName } })
        {
            throw new UnauthorizedAccessException("Only the aspire module may push resource change signals.");
        }

        foreach (var change in changes)
        {
            await Publish(change);
        }
    }

    public async Task<AspireCommandResult> Execute(string resource, string command)
    {
        var topology = _store?.Snapshot() ?? RuntimeTopology.Disconnected;
        var decision = CommandPolicy.Evaluate(
            topology,
            IsDirectPerson(Context.Caller),
            resource,
            command,
            _self,
            _options.PersonOnlyCommands);

        if (decision is RefuseCommand refused)
        {
            return refused.ToResult();
        }

        await DropStale();

        if (decision is AllowCommandNow allow)
        {
            return await RunCommand(allow.Instance.Id, allow.Command.Name, Pending(), planId: null);
        }

        var approval = (RequireCommandApproval)decision;
        if (Pending().FirstOrDefault(p => p.InstanceId == approval.Instance.Id && p.Command == approval.Command.Name) is { } waiting)
        {
            return new AspireCommandResult(approval.Instance.Id, approval.Command.Name, false, RefusalReasons.Pending, Confirmation: waiting.PlanId);
        }

        if (Pending().Length >= MaxPending)
        {
            return new AspireCommandResult(approval.Instance.Id, approval.Command.Name, false, RefusalReasons.Declined,
                Message: $"{MaxPending} commands already wait for the person's answer.");
        }

        var displayName = string.IsNullOrWhiteSpace(approval.Command.DisplayName) ? approval.Command.Name : approval.Command.DisplayName;
        var plan = new ConfirmationPlan(
            $"{CommandKind}:{approval.Instance.Id}:{approval.Command.Name}:{Guid.NewGuid():N}",
            ConfirmationAction.Other,
            false,
            approval.CardTitle,
            approval.CardBody,
            displayName,
            "Cancel",
            approval.Instance.Id)
        {
            RequiresPerson = true,
            Kind = CommandKind,
            Subject = $"{approval.Instance.Id}:{approval.Command.Name}",
            Expiry = _options.CommandConfirmationExpiry,
        };

        ConfirmationResult? immediate;
#pragma warning disable CA1031 // a refused question is a returned value and a signal, never a throw
        try
        {
            immediate = await (Confirmations ?? throw new InvalidOperationException("No confirmations are hosted in this runtime.")).Request(plan);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = AspireLiveTelemetry.Sanitize(error.Message);
            await Save(State, new AspireCommandRefused(approval.Instance.Id, approval.Command.Name, RefusalReasons.Declined, message));
            return new AspireCommandResult(approval.Instance.Id, approval.Command.Name, false, RefusalReasons.Declined, Message: message);
        }
#pragma warning restore CA1031

        if (immediate is not null)
        {
            await Save(State, new AspireCommandRefused(approval.Instance.Id, approval.Command.Name, RefusalReasons.Declined));
            return new AspireCommandResult(approval.Instance.Id, approval.Command.Name, false, RefusalReasons.Declined, Confirmation: plan.Id);
        }

        var entry = new AspirePendingCommand(plan.Id, approval.ModelName, approval.Instance.Id, approval.Command.Name, Context.Time.GetUtcNow());
        await Save(State with { PendingCommands = [.. Pending(), entry] },
            new AspireCommandRequested(approval.Instance.Id, approval.Command.Name, plan.Id));
        return new AspireCommandResult(approval.Instance.Id, approval.Command.Name, false, RefusalReasons.Pending, Confirmation: plan.Id);
    }

    [On]
    public async Task OnCommandAnswered(ConfirmationResolved resolved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ct.ThrowIfCancellationRequested();
        await DropStale();

        if (Pending().FirstOrDefault(p => p.PlanId == resolved.PlanId) is not { } pending)
        {
            return;
        }

        var outcome = Confirmations is { } confirmations ? await confirmations.Outcome(resolved.PlanId) : null;
        var rest = Without(pending.PlanId);

        if (outcome is not { Approved: true, Skipped: false, Approver.IsHuman: true })
        {
            await Save(State with { PendingCommands = rest }, new AspireCommandRefused(pending.InstanceId, pending.Command, RefusalReasons.Declined));
            return;
        }

        // Re-run policy steps 1–5 against the live topology before executing.
        var topology = _store?.Snapshot() ?? RuntimeTopology.Disconnected;
        var recheck = CommandPolicy.Evaluate(
            topology,
            isDirectPerson: false,
            pending.InstanceId,
            pending.Command,
            _self,
            _options.PersonOnlyCommands);

        if (recheck is RefuseCommand refused)
        {
            await Save(State with { PendingCommands = rest },
                new AspireCommandRefused(pending.InstanceId, pending.Command, refused.Reason, refused.Message));
            return;
        }

        await RunCommand(pending.InstanceId, pending.Command, rest ?? [], pending.PlanId);
    }

    public async Task RecordLogAsync(AspireLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sanitized = entry with
        {
            Message = AspireLiveTelemetry.Sanitize(entry.Message) ?? string.Empty,
            Exception = AspireLiveTelemetry.Sanitize(entry.Exception),
        };
        var nextLogs = State.StructuredLogs.Append(sanitized).TakeLast(200).ToArray();
        var next = State with { StructuredLogs = nextLogs };
        await Save(next, new AspireTelemetryChanged(next.StructuredLogs.Length, next.Traces.Length));
    }

    public async Task RecordTraceAsync(AspireTraceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        var sanitized = span with { Error = AspireLiveTelemetry.Sanitize(span.Error) };
        var nextTraces = State.Traces.Append(sanitized).TakeLast(200).ToArray();
        var next = State with { Traces = nextTraces };
        await Save(next, new AspireTelemetryChanged(next.StructuredLogs.Length, next.Traces.Length));
    }

    private async Task<AspireCommandResult> RunCommand(string instanceId, string commandName, AspirePendingCommand[] remaining, string? planId)
    {
        var nextState = State with { PendingCommands = remaining.Length == 0 ? null : remaining };
        if (_bridge is null || _store is not { Connected: true })
        {
            await Save(nextState, new AspireCommandRefused(instanceId, commandName, RefusalReasons.Unavailable));
            return new AspireCommandResult(instanceId, commandName, false, RefusalReasons.Unavailable, Confirmation: planId);
        }

        BridgeCommandReply reply;
#pragma warning disable CA1031
        try
        {
            reply = await _bridge.ExecuteAsync(instanceId, commandName, _options.CommandTimeout, CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = AspireLiveTelemetry.Sanitize(error.Message);
            await Save(nextState, new AspireCommandRefused(instanceId, commandName, RefusalReasons.Failed, message));
            return new AspireCommandResult(instanceId, commandName, false, RefusalReasons.Failed, Message: message, Confirmation: planId);
        }
#pragma warning restore CA1031

        if (!reply.Succeeded)
        {
            var reason = _store.Connected ? RefusalReasons.Failed : RefusalReasons.Unavailable;
            var message = AspireLiveTelemetry.Sanitize(reply.Message);
            await Save(nextState, new AspireCommandRefused(instanceId, commandName, reason, message));
            return new AspireCommandResult(instanceId, commandName, false, reason, Message: message, Confirmation: planId);
        }

        await Save(nextState, new AspireCommandExecuted(instanceId, commandName, Context.Time.GetUtcNow()));
        return new AspireCommandResult(instanceId, commandName, true, Confirmation: planId);
    }

    private AspirePendingCommand[] Pending() => State.PendingCommands ?? [];

    private TimeSpan StaleAfter => 3 * _options.CommandConfirmationExpiry;

    private async Task DropStale()
    {
        var cutoff = Context.Time.GetUtcNow() - StaleAfter;
        var stale = Pending().Where(item => item.RequestedAt < cutoff).ToArray();
        if (stale.Length == 0) { return; }
        var kept = Pending().Except(stale).ToArray();
        await Save(State with { PendingCommands = kept.Length == 0 ? null : kept },
            [.. stale.Select(item => (DigitalBrain.Signal)new AspireCommandRefused(item.InstanceId, item.Command, RefusalReasons.Declined, "expired"))]);
    }

    private AspirePendingCommand[]? Without(string planId)
        => Pending().Where(item => item.PlanId != planId).ToArray() is { Length: > 0 } rest ? rest : null;

    private static bool IsDirectPerson(Caller? caller)
        => caller is { Direct: true, Conversation: null, Actor: Actor.Person };
}
