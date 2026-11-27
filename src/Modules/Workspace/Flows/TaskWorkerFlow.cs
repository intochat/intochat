using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps.Tasks;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Web;
using Microsoft.Extensions.Logging;
using TaskStatus = IntoChat.OS.Modules.Apps.Tasks.TaskStatus;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Persisted state of a <see cref="TaskWorkerFlow"/> run.
/// </summary>
internal sealed class TaskWorkerInner : AgentInner
{
    public string? TaskId { get; set; }

    public string Goal { get; set; } = "";

    public string[] Contracts { get; set; } = [];

    public string? Summary { get; set; }

    public TaskAskPrompt? PendingAsk { get; set; }

    public string? AskPlanId { get; set; }

    public int? SelectedOptionIndex { get; set; }

    public string? SelectedOption { get; set; }

    public TaskArtifactLink[] Artifacts { get; set; } = [];

    public DateTimeOffset? AskExpires { get; set; }
}

/// <summary>
/// Tool-equipped durable worker flow (<c>flow.task-worker</c>) that executes a background workspace task
/// with an explicit least-privilege contract subset, supports person-only multi-option <c>Ask</c> pauses
/// (<c>NeedsYou</c>), and keeps the linked <see cref="ITasks"/> item synchronized across its lifecycle.
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed class TaskWorkerFlow(AgentStepOptions? options = null) : AgentFlow<TaskWorkerInner>(options), IFlowStarter
{
    public const string Alias = "flow.task-worker";
    public const string AskConfirmationKind = "task.ask";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] StepNames = ["execute", "ask", "finalize"];
    private static readonly TimeSpan AskWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// Default least-privilege contracts equipped on a background task worker agent.
    /// Excludes <see cref="ITasks"/> because <see cref="TaskWorkerFlow"/> itself synchronizes task state
    /// from structured worker replies, preventing worker agents from bypassing <c>NeedsYou</c> gates.
    /// </summary>
    public static readonly string[] DefaultContracts =
    [
        typeof(IFiles).FullName!,
        typeof(IEngineerRuns).FullName!,
        typeof(IRepository).FullName!,
        typeof(IWebSearch).FullName!,
        typeof(IWebRead).FullName!,
        typeof(ICodeOrchestration).FullName!,
    ];

    private static readonly HashSet<string> AllowedContracts = new(DefaultContracts, StringComparer.Ordinal);

    private ITasks TasksProxy => Context.Get<ITasks>(ITasks.Key(Id.Brain));

    private IConfirmationRequests ConfirmationsProxy => Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    protected override IReadOnlyList<string> Steps => StepNames;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Wires.Ensure<TasksChanged>(ITasks.Key(Id.Brain)).ConfigureAwait(true);
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Resolves the effective least-privilege contract subset for a task worker. When <paramref name="requested"/>
    /// is null or empty, returns <see cref="DefaultContracts"/>. When a non-empty allowlist is requested, intersects
    /// it with <see cref="DefaultContracts"/> and fails closed (returning <c>[]</c>) if none are allowed so a
    /// disallowed request never widens privileges.
    /// </summary>
    public static string[] ResolveContracts(string[]? requested)
    {
        if (requested is null || requested.Length == 0)
        {
            return [.. DefaultContracts];
        }

        return requested
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Where(AllowedContracts.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<TaskWorkerInner>(innerJson, Json) ?? new TaskWorkerInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead(
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

    protected override AgentDefinition BuildAgentDefinition(string step, TaskWorkerInner inner)
        => new()
        {
            DisplayName = "Task worker",
            Description = "Executes a background workspace task with least-privilege tools.",
            Instructions =
                "You are a durable background task worker. Use your equipped tools to complete the assigned task. " +
                "If a human decision is required, reply with JSON: {\"ask\":{\"question\":\"...\",\"options\":[\"Option A\",\"Option B\"],\"recommendedIndex\":0}}. " +
                "Otherwise reply with JSON: {\"summary\":\"...\",\"artifacts\":[{\"kind\":\"pr\",\"title\":\"...\",\"reference\":\"...\"}]}.",
            Contracts = ResolveContracts(inner.Contracts),
        };

    protected override async Task<StepResult<TaskWorkerInner>> RunStep(string step, TaskWorkerInner inner, CancellationToken ct)
    {
        try
        {
            switch (step)
            {
                case "execute":
                    await TryTransitionTaskAsync(inner.TaskId, TaskStatus.InProgress, null).ConfigureAwait(true);
                    return await Ask(step, inner, BuildPrompt(inner)).ConfigureAwait(true);

                case "ask":
                    {
                        var parsed = ParseWorkerReply(inner.Reply);
                        if (parsed.Artifacts.Length > 0)
                        {
                            inner.Artifacts = parsed.Artifacts;
                        }
                        if (!string.IsNullOrWhiteSpace(parsed.Summary))
                        {
                            inner.Summary = parsed.Summary;
                        }

                        if (parsed.Ask is null)
                        {
                            return new StepResult<TaskWorkerInner>(inner, Disposition: StepDisposition.Skip, Reason: "No human decision needed.");
                        }

                        inner.PendingAsk = parsed.Ask;
                        inner.AskPlanId ??= $"task-ask:{Id.Key}";
                        inner.AskExpires = Context.Time.GetUtcNow() + AskWindow;
                        await TryAskTaskAsync(inner.TaskId, parsed.Ask).ConfigureAwait(true);

                        var plan = PlanFor(step, inner);
                        var immediate = await ConfirmationsProxy.Request(plan).ConfigureAwait(true);
                        if (immediate is { } answer)
                        {
                            inner.SelectedOptionIndex = answer.SelectedOptionIndex;
                            inner.SelectedOption = answer.SelectedOption;
                            if (answer.Approved)
                            {
                                return inner.Advance();
                            }

                            throw new FlowFailed(step, $"Ask {answer.Outcome}: {answer.Reason}");
                        }

                        return new StepResult<TaskWorkerInner>(
                            inner,
                            new Waiting(step, WaitReason.External, inner.AskExpires, inner.AskPlanId),
                            Disposition: StepDisposition.Wait);
                    }

                case "finalize":
                    {
                        var summary = !string.IsNullOrWhiteSpace(inner.Summary)
                            ? inner.Summary!
                            : !string.IsNullOrWhiteSpace(inner.SelectedOption)
                                ? $"Completed with human choice: {inner.SelectedOption}"
                                : (!string.IsNullOrWhiteSpace(inner.Reply) ? inner.Reply!.Trim() : $"Completed {State.Title}");
                        inner.Summary = summary;

                        foreach (var artifact in inner.Artifacts)
                        {
                            await TryLinkArtifactAsync(inner.TaskId, artifact).ConfigureAwait(true);
                        }
                        await TryTransitionTaskAsync(inner.TaskId, TaskStatus.Completed, summary).ConfigureAwait(true);
                        return inner.Advance();
                    }

                default:
                    throw new FlowFailed(step, $"Unknown step {step}.");
            }
        }
        catch (FlowFailed failed)
        {
            await TryTransitionTaskAsync(inner.TaskId, TaskStatus.Failed, failed.Message).ConfigureAwait(true);
            throw;
        }
    }

    protected override ConfirmationPlan PlanFor(string step, TaskWorkerInner inner)
    {
        if (inner.PendingAsk is { } prompt)
        {
            var planId = inner.AskPlanId ?? $"task-ask:{Id.Key}";
            return ConfirmationPlan.Ask(
                id: planId,
                question: prompt.Question,
                options: prompt.Options,
                recommended: prompt.RecommendedIndex,
                targetLabel: inner.TaskId ?? State.Title,
                expiry: AskWindow,
                kind: AskConfirmationKind,
                subject: inner.TaskId ?? Id.ToString());
        }

        return base.PlanFor(step, inner);
    }

    [On]
    public async Task OnTaskConfirmationResolved(ConfirmationResolved resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (State is not { Status: FlowStatus.Waiting, Waiting: { Step: "ask", Reason: WaitReason.External, ConfirmationPlanId: { } planId } }
            || !string.Equals(planId, resolved.PlanId, StringComparison.Ordinal))
        {
            return;
        }

        var outcome = await ConfirmationsProxy.Outcome(planId).ConfigureAwait(true);
        if (outcome is not { } answer)
        {
            return;
        }

        State.Inner.SelectedOptionIndex = answer.SelectedOptionIndex;
        State.Inner.SelectedOption = answer.SelectedOption;

        var waitOutcome = answer.Outcome switch
        {
            ConfirmationOutcome.Approved => WaitOutcome.Approved,
            ConfirmationOutcome.Expired => WaitOutcome.Expired,
            _ => WaitOutcome.Denied,
        };
        if (!answer.Approved)
        {
            await TryTransitionTaskAsync(State.Inner.TaskId, TaskStatus.Failed, $"Ask {answer.Outcome}: {answer.Reason}").ConfigureAwait(true);
        }

        await Resume("ask", waitOutcome, answer.Approver?.Id ?? resolved.Approver?.Id).ConfigureAwait(true);
    }

    [On]
    public async Task OnTasksChanged(TasksChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (State is not { Status: FlowStatus.Waiting, Waiting: { Step: "ask", Reason: WaitReason.External } waiting }
            || string.IsNullOrWhiteSpace(State.Inner.TaskId)
            || !string.Equals(changed.TaskId, State.Inner.TaskId, StringComparison.Ordinal))
        {
            return;
        }

        TaskRecord? task;
        try
        {
            task = await TasksProxy.Read(State.Inner.TaskId).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            ConsoleLogger.LogSwallowed(error, $"TaskWorkerFlow could not read task '{State.Inner.TaskId}' on TasksChanged.");
            return;
        }

        if (task is null || task.PendingAsk is not null || task.LastAnswer is not { } answer)
        {
            return;
        }

        State.Inner.SelectedOptionIndex = answer.SelectedOptionIndex;
        State.Inner.SelectedOption = answer.SelectedOption;

        if (!string.IsNullOrWhiteSpace(waiting.ConfirmationPlanId))
        {
            try
            {
                await ConfirmationsProxy
                    .Withdraw(waiting.ConfirmationPlanId)
                    .ConfigureAwait(true);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                ConsoleLogger.LogSwallowed(error, $"TaskWorkerFlow could not withdraw confirmation '{waiting.ConfirmationPlanId}' after ITasks.AnswerAsk.");
            }
        }

        await Resume("ask", WaitOutcome.Approved, answer.AnsweredBy).ConfigureAwait(true);
    }

    private string BuildPrompt(TaskWorkerInner inner)
    {
        var goal = string.IsNullOrWhiteSpace(inner.Goal) ? State.Title : inner.Goal;
        return $"Task: {State.Title}\nGoal: {goal}\nUse your tools to complete this task or ask a person-only multi-option question if a decision is required.";
    }

    private sealed record WorkerReply(string? Summary, TaskAskPrompt? Ask, TaskArtifactLink[] Artifacts);

    private static WorkerReply ParseWorkerReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return new WorkerReply(null, null, []);
        }

        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return new WorkerReply(reply.Trim(), null, []);
        }

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new WorkerReply(reply.Trim(), null, []);
            }

            var root = doc.RootElement;
            string? summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;

            TaskAskPrompt? ask = null;
            if (root.TryGetProperty("ask", out var askEl) && askEl.ValueKind == JsonValueKind.Object)
            {
                var q = askEl.TryGetProperty("question", out var qEl) && qEl.ValueKind == JsonValueKind.String
                    ? qEl.GetString()
                    : null;
                var opts = askEl.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Array
                    ? oEl.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.String).Select(static e => e.GetString() ?? "").Where(static x => !string.IsNullOrWhiteSpace(x)).ToArray()
                    : [];
                int? rec = askEl.TryGetProperty("recommendedIndex", out var rEl) && rEl.ValueKind == JsonValueKind.Number && rEl.TryGetInt32(out var idx)
                    ? idx
                    : null;
                if (!string.IsNullOrWhiteSpace(q) && opts.Length >= 2)
                {
                    if (rec is { } r && (r < 0 || r >= opts.Length)) { rec = null; }
                    ask = new TaskAskPrompt(q.Trim(), opts, rec);
                }
            }

            var artifacts = new List<TaskArtifactLink>();
            if (root.TryGetProperty("artifacts", out var artEl) && artEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { continue; }
                    var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                    var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                        ? l.GetString()
                        : (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null);
                    var uri = item.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String
                        ? u.GetString()
                        : (item.TryGetProperty("reference", out var rf) && rf.ValueKind == JsonValueKind.String ? rf.GetString() : null);
                    var sha = item.TryGetProperty("commitSha", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(uri))
                    {
                        artifacts.Add(new TaskArtifactLink(kind.Trim(), label.Trim(), uri.Trim(), string.IsNullOrWhiteSpace(sha) ? null : sha.Trim()));
                    }
                }
            }

            return new WorkerReply(summary, ask, [.. artifacts]);
        }
        catch (JsonException)
        {
            return new WorkerReply(reply.Trim(), null, []);
        }
    }

    private static readonly ILogger ConsoleLogger = OsConsoleLog.For<TaskWorkerFlow>();

    private async Task TryTransitionTaskAsync(string? taskId, TaskStatus status, string? summary)
    {
        if (string.IsNullOrWhiteSpace(taskId)) { return; }
        try
        {
            await TasksProxy.Transition(taskId, status, summary).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            ConsoleLogger.LogSwallowed(error, $"TaskWorkerFlow could not transition linked task '{taskId}' to {status}.");
        }
    }

    private async Task TryAskTaskAsync(string? taskId, TaskAskPrompt prompt)
    {
        if (string.IsNullOrWhiteSpace(taskId)) { return; }
        try
        {
            await TasksProxy.Ask(taskId, prompt).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            ConsoleLogger.LogSwallowed(error, $"TaskWorkerFlow could not attach ask prompt to linked task '{taskId}'.");
        }
    }

    private async Task TryLinkArtifactAsync(string? taskId, TaskArtifactLink artifact)
    {
        if (string.IsNullOrWhiteSpace(taskId)) { return; }
        try
        {
            await TasksProxy.LinkArtifact(taskId, artifact).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            ConsoleLogger.LogSwallowed(error, $"TaskWorkerFlow could not link artifact to linked task '{taskId}'.");
        }
    }
}
