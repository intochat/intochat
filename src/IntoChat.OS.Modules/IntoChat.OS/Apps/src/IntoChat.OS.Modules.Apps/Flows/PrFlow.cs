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
}

// One pull request: open (the repository's CreatePullRequest; skipped once a number is held) and merged (an External wait). The module
// publishes no merge signal of its own: its PullRequestChanged for the held number makes the flow read the pull request, and a closed
// one with a merge sha ends the wait (closed unmerged cancels the flow).
[DigitalBrain.Neuron(Alias)]
public sealed class PrFlow : Flow<PrInner>, IFlowStarter
{
    public const string Alias = "flow.pr";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Names = ["open", "merged"];

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
                // Merged before the wait began: no signal will come for it.
                return await Merged(inner) switch
                {
                    true => new StepResult<PrInner>(inner),
                    false => throw new FlowFailed(step, $"pull request {inner.PullRequestNumber} closed without merging"),
                    null => new StepResult<PrInner>(inner, new Waiting(step, WaitReason.External, null, null)),
                };
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    [On]
    public async Task OnPullRequestChanged(PullRequestChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (State is not { Status: FlowStatus.Waiting, Waiting.Step: "merged" } || changed.Number != State.Inner.PullRequestNumber) { return; }
        switch (await Merged(State.Inner))
        {
            case true: await Resume("merged", WaitOutcome.Arrived, null); break;
            case false: await Resume("merged", WaitOutcome.Denied, null); break;
        }
    }

    // True when merged, false when closed unmerged, null while open. The read is of this flow's repository, so a change of the same
    // number in another repository changes nothing.
    private async Task<bool?> Merged(PrInner inner)
    {
        var number = inner.PullRequestNumber ?? throw new FlowFailed("merged", "no pull request was opened");
        var snapshot = (await Repository(inner).ReadPullRequest(new ReadPullRequest(number))).Snapshot;
        return snapshot.IsOpen ? null : snapshot.MergeSha is { Length: > 0 };
    }

    private IRepository Repository(PrInner inner)
        => NeuronId.TryParse(inner.Repository, out var id) ? Context.Get<IRepository>(id) : throw new FlowFailed("open", $"{inner.Repository} is not a repository id");
}
