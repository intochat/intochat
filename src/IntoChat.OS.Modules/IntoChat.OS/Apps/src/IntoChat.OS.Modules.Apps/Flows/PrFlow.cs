using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;

namespace IntoChat.OS.Modules.Apps.Flows;

// A pull request's own state: Repository is the GitHub repository neuron's id ({brain}/github/{connection}); open fills
// PullRequestNumber.
public sealed class PrInner
{
    public string Repository { get; set; } = "";

    public string Branch { get; set; } = "";

    public string BaseBranch { get; set; } = "main";

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public int? PullRequestNumber { get; set; }

    // When merged first waited, for the 30-day staleness bound.
    public DateTimeOffset? WaitingSince { get; set; }

    // The re-read period of the merged wait.
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromHours(1);
}

// One pull request: open (the repository's CreatePullRequest; skipped once a number is held) and merged. Merged reads the pull
// request: merged completes, closed unmerged fails ("closed without merge"), open waits on a schedule (PollEvery). The module has no
// merge signal: its PullRequestChanged for the held number is the fast path, read and resumed at once. A missed signal is caught by
// the scheduled re-read, which waits again while the pull request is open, up to 30 days ("pull request stale").
[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class PrFlow : Flow<PrInner>, IFlowStarter
{
    public const string Alias = "flow.pr";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Names = ["open", "merged"];

    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    protected override IReadOnlyList<string> Steps => Names;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<PrInner>(innerJson, Json) ?? new PrInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await Context.Synapses.EnsureBrainWide(typeof(PullRequestChanged));
        await base.OnActivate(cancellationToken);
    }

    protected override async Task<StepResult<PrInner>> Run(string step, PrInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "open":
                if (inner.PullRequestNumber is null)
                {
                    var opened = await Repository(inner).CreatePullRequest(new CreatePullRequest(inner.Title.Length > 0 ? inner.Title : State.Title, inner.Branch, inner.BaseBranch, inner.Body));
                    inner.PullRequestNumber = opened.Number;
                }
                return new StepResult<PrInner>(inner);
            case "merged":
                inner.WaitingSince ??= Context.Time.GetUtcNow();
                return await Check(inner) ? new StepResult<PrInner>(inner) : new StepResult<PrInner>(inner, Poll(inner));
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    [On]
    public async Task OnPullRequestChanged(PullRequestChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (!WaitingOnMerged || changed.Number != State.Inner.PullRequestNumber) { return; }
        await Reconcile();
    }

    // The schedule wait's expiry re-reads the pull request instead of ending the wait unread; other reminders go to the base.
    protected override async Task OnReminder(string name, CancellationToken cancellationToken)
    {
        if (name == ExpiryReminder && WaitingOnMerged && State.Waiting!.Expires is { } expires && Context.Time.GetUtcNow() >= expires)
        {
            await Reconcile();
            return;
        }
        await base.OnReminder(name, cancellationToken);
    }

    private bool WaitingOnMerged => State is { Status: FlowStatus.Waiting, Waiting: { Step: "merged", Reason: WaitReason.Schedule } };

    // Merged resumes the step; closed unmerged or stale fails the flow; still open waits another period.
    private async Task Reconcile()
    {
        bool merged;
        try { merged = await Check(State.Inner); }
        catch (FlowFailed failed)
        {
            await Fail(failed.Message);
            return;
        }
        if (merged) { await Resume("merged", WaitOutcome.Arrived, null); return; }
        var next = Poll(State.Inner);
        await Save(State with { Waiting = State.Waiting! with { Expires = next.Expires } }, new FlowWaiting(Id.ToString(), "merged", WaitReason.Schedule, null));
    }

    // True when merged; throws FlowFailed when closed unmerged or stale; false while open.
    private async Task<bool> Check(PrInner inner)
    {
        var number = inner.PullRequestNumber ?? throw new FlowFailed("merged", "no pull request was opened");
        var snapshot = (await Repository(inner).ReadPullRequest(new ReadPullRequest(number))).Snapshot;
        if (!snapshot.IsOpen) { return snapshot.MergeSha is { Length: > 0 } ? true : throw new FlowFailed("merged", "closed without merge"); }
        if (Context.Time.GetUtcNow() - (inner.WaitingSince ?? Context.Time.GetUtcNow()) > StaleAfter) { throw new FlowFailed("merged", "pull request stale"); }
        return false;
    }

    private Waiting Poll(PrInner inner) => new("merged", WaitReason.Schedule, Context.Time.GetUtcNow() + inner.PollEvery, null);

    // Fails the waiting flow as the base fails a step: the step's FlowStep and the flow's FlowDone in one save.
    private async Task Fail(string detail)
    {
        var flowId = Id.ToString();
        await Save(State with { Status = FlowStatus.Failed, Waiting = null, Ended = Context.Time.GetUtcNow() },
            [new FlowStep(flowId, "merged", StepOutcome.Failed, detail), new FlowDone(flowId, FlowOutcome.Failed, detail)]);
        await Context.Reminders.Stop(ExpiryReminder);
    }

    private IRepository Repository(PrInner inner)
        => NeuronId.TryParse(inner.Repository, out var id) ? Context.Get<IRepository>(id) : throw new FlowFailed("open", $"{inner.Repository} is not a repository id");
}
