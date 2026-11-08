using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Modules.Apps.Flows;

// A build flow's own state. Kind "unit" builds a synapse unit from Goal and deploys it under the flow's key; kind "app" builds the
// app AppKey for its BeginBuild Attempt and lands the files through CompleteBuild. Draft fills Files (and an app's Manifest); deploy
// fills Hash (a unit's build hash, or an app's landed version).
public sealed class BuildInner
{
    public const string Unit = "unit";
    public const string App = "app";

    public string Kind { get; set; } = Unit;

    public string Goal { get; set; } = "";

    public string? AppKey { get; set; }

    public string? Instruction { get; set; }

    public Guid? Attempt { get; set; }

    public Dictionary<string, string>? Files { get; set; }

    public AppManifest? Manifest { get; set; }

    public string? Hash { get; set; }

    public string? Diagnostics { get; set; }

    public DateTimeOffset? ApprovalExpires { get; set; }
}

// The flow's own deferred start: StartFromJson returns at once and the steps run in this later turn, so the caller (an endpoint, Ino)
// does not wait for the Builder.
public interface IBuildFlow : IFlowStarter
{
    Task Begin(string title, string owner, string? parentFlowId, string innerJson);
}

// One build: draft (the Creator's Builder loop), check (the compile the deploy will run), deploy (the studio for a unit, CompleteBuild for
// an app) and approve (a two-day approval of the deployed build). Each step is idempotent on rerun: deploy keys the studio by source
// hash and CompleteBuild by attempt. Check is an inline call: no compile IOperation exists.
[DigitalBrain.Neuron(Alias)]
public sealed class BuildFlow : Flow<BuildInner>, IBuildFlow
{
    public const string Alias = "flow.build";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Names = ["draft", "check", "deploy", "approve"];

    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromDays(2);

    protected override IReadOnlyList<string> Steps => Names;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        // Not awaited: the call queues behind this turn and runs the steps there.
        _ = Context.Get<IBuildFlow>(Id).Begin(title, owner, parentFlowId, innerJson);
        return Task.CompletedTask;
    }

    public Task Begin(string title, string owner, string? parentFlowId, string innerJson)
        => Start(title, owner, parentFlowId, JsonSerializer.Deserialize<BuildInner>(innerJson, Json) ?? new BuildInner());

    public Task<FlowHead> Head() => Task.FromResult(new FlowHead([.. Names], State.Owner, State.TraceId, State.ParentFlowId, State.Started,
        JsonSerializer.Serialize(State.Inner, Json), State.Title, State.Status, [.. State.Done], State.Ended, null));

    // A step whose call failed because the host is stopping is not a failed build: it saves nothing and reruns when the flow next
    // activates, as after a crash. An app build that fails ends its attempt Failed, so the app does not stay Building.
    protected override async Task<StepResult<BuildInner>> Run(string step, BuildInner inner, CancellationToken ct)
    {
        try { return await RunStep(step, inner, ct); }
        catch (Exception error) when (error is not OperationCanceledException and not FlowFailed && Stopping)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException && inner is { Kind: BuildInner.App, AppKey: not null, Attempt: { } attempt })
        {
            await App(inner).CompleteBuild(attempt, null, error.Message);
            throw;
        }
    }

    private async Task<StepResult<BuildInner>> RunStep(string step, BuildInner inner, CancellationToken ct)
    {
        switch (step)
        {
            case "draft":
                {
                    var drafter = Context.Services.GetRequiredService<IDrafter>();
                    var snapshot = inner.Kind == BuildInner.App ? await App(inner).Read() : null;
                    var request = new BuildRequest(Id.Brain.Value, inner.Kind, inner.Goal, inner.AppKey, inner.Instruction, snapshot);
                    // The Builder's model calls run off the flow's scheduler.
                    var draft = await Task.Run(() => drafter.Draft(request, ct), ct);
                    inner.Files = draft.Files;
                    inner.Manifest = draft.Manifest;
                    return new StepResult<BuildInner>(inner);
                }
            case "check":
                inner.Diagnostics = await Check(inner, ct);
                return new StepResult<BuildInner>(inner);
            case "deploy":
                inner.Hash = inner.Kind == BuildInner.App ? await LandApp(inner) : await DeployUnit(inner);
                return new StepResult<BuildInner>(inner);
            case "approve":
                inner.ApprovalExpires = Context.Time.GetUtcNow() + ApprovalWindow;
                return new StepResult<BuildInner>(inner, new Waiting(step, WaitReason.Approval, inner.ApprovalExpires, null));
            default:
                throw new FlowFailed(step, $"Unknown step {step}.");
        }
    }

    protected override ConfirmationPlan PlanFor(string step, BuildInner inner)
        => base.PlanFor(step, inner) with
        {
            Title = $"Deploy {inner.Goal}",
            Body = (inner.Hash is null ? "" : $"Build {inner.Hash}. ") + (string.IsNullOrWhiteSpace(inner.Diagnostics) ? "No diagnostics." : inner.Diagnostics),
        };

    private async Task<string> Check(BuildInner inner, CancellationToken ct)
    {
        var files = inner.Files is { Count: > 0 } drafted ? drafted : throw new FlowFailed("check", "the draft has no files");
        if (inner.Kind == BuildInner.App && inner.Manifest is not null)
        {
            var existing = (await App(inner).Read()).Files;
            var checkedProgram = await Context.Services.GetRequiredService<Creator>().CheckProgram(existing, files, ct);
            if (checkedProgram is { Success: false }) { throw new FlowFailed("check", JsonSerializer.Serialize(checkedProgram, Json)); }
            return checkedProgram is null ? "" : JsonSerializer.Serialize(checkedProgram.Errors, Json);
        }
        if (files.Count == 1)
        {
            var result = await Studio().Check(files.Values.Single());
            return result.Ok ? result.Text : throw new FlowFailed("check", result.Text);
        }
        var unit = SynapseCheck.Compile(Context.Services, files) ?? throw new FlowFailed("check", "the draft is not synapse source");
        return unit.Success ? JsonSerializer.Serialize(unit.Errors, Json) : throw new FlowFailed("check", JsonSerializer.Serialize(unit, Json));
    }

    // A unit's alias is the flow's key; the studio deploys the same source once.
    private async Task<string> DeployUnit(BuildInner inner)
    {
        var (hash, failure) = await HeadlessUnit.Deploy(Studio(), Id.Key, inner.Files!);
        return hash ?? throw new FlowFailed("deploy", failure ?? "the unit did not deploy");
    }

    // An app lands as Creator.Build did: a headless app as its synapse unit (alias = app name), any other through CompleteBuild.
    private async Task<string> LandApp(BuildInner inner)
    {
        var app = App(inner);
        var attempt = inner.Attempt ?? throw new FlowFailed("deploy", "the build has no attempt");
        if (inner.Manifest is null)
        {
            var key = inner.AppKey!;
            var name = key[(key.LastIndexOf("/apps/", StringComparison.Ordinal) + "/apps/".Length)..];
            var failure = await HeadlessUnit.Complete(app, attempt, Studio(), name, inner.Files!);
            if (failure is not null) { throw new FlowFailed("deploy", failure); }
            return (await Studio().List()).FirstOrDefault(unit => unit.SourceKey == name)?.Hash ?? "";
        }
        var completed = await app.CompleteBuild(attempt, inner.Files, null, inner.Manifest);
        return completed.Status == AppStatus.Live ? completed.Version
            : throw new FlowFailed("deploy", completed.Activity.LastOrDefault() ?? "the build did not complete");
    }

    private IApp App(BuildInner inner)
    {
        var key = inner.AppKey ?? throw new FlowFailed("draft", "an app build names no app");
        return Context.Get<IApp>(new NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, key[(key.IndexOf('/', StringComparison.Ordinal) + 1)..]));
    }

    private bool Stopping => Context.Services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.IsCancellationRequested == true;

    private ISynapseStudio Studio() => Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value));
}
