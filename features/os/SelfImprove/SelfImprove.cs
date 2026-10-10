using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Microsoft.Aspire;

// Shared by every synapse: the app's key, its brain, the change-set shape and the list rendering.
public static class SelfImprove
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static bool Ended(CandidateStage stage) => stage is CandidateStage.Promoted or CandidateStage.Discarded or CandidateStage.Failed;

    public sealed record ChangeSet(string Branch, string Summary, Dictionary<string, string> Files, PlanJson Plan);
    public sealed record PlanJson(CallJson[] Calls, string[] Spans, string Budget);
    public sealed record CallJson(string Contract, string Key, string Method, string Arguments, string Expected);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CandidateProposal Parse(string input)
    {
        var change = JsonSerializer.Deserialize<ChangeSet>(input, Json) ?? throw new ArgumentException("A change set is a JSON object.");
        var plan = new VerificationPlan(
            [.. change.Plan.Calls.Select(call => new NeuronExpectation(call.Contract, call.Key, call.Method, call.Arguments, call.Expected))],
            change.Plan.Spans, TimeSpan.Parse(change.Plan.Budget, System.Globalization.CultureInfo.InvariantCulture));
        return new(change.Branch, change.Summary, change.Files, plan);
    }

    // The list is rebuilt from the kernel's candidate instances and the cluster every time, never patched.
    public static async Task RenderListAsync(DigitalBrainConnection brain)
    {
        var lines = new List<string>();
        await foreach (var instance in brain.Kernel.Instances<ICandidate>(brain.Stopping))
        {
            var candidate = await brain.Get<ICandidate>(instance.Key).Read();
            lines.Add($"- `{candidate.Sha}` — {candidate.Stage} — {candidate.Summary}");
        }
        var cluster = await brain.Get<IDeployment>(IDeployment.Key(Scope(brain))).Read();
        var live = $"Live: `{cluster.CurrentSha}` — {cluster.Stage}";
        await brain.Get<IText>(Key(brain) + "/revisions").Set(lines.Count == 0 ? "_No candidates yet._\n\n" + live : string.Join("\n", lines) + "\n\n" + live);
    }

    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        string Name(string part) => appKey + "/" + part;
        var main = brain.Get<ILayout>(Name("main"));
        await main.Set(new LayoutDefinition("column", [new UiChildRef(UIVocabulary.TextType, Name("revisions")), new UiChildRef(UIVocabulary.TextType, Name("report"))], Extents: [0, 160]), (await main.Read()).Revision);
        var surface = brain.Get<ISurface>(Name("surface"));
        await surface.Set(new SurfaceDefinition("Self-improve", [new UiChildRef(UIVocabulary.LayoutType, Name("main"))]), (await surface.Read()).Revision);
    }

    public static Task OpenWindowAsync(DigitalBrainConnection brain, CancellationToken ct)
        => brain.Get<IWorkspace>(Scope(brain)).EnsureOpenAsync(Key(brain) + "/window", "Self-improve",
            WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Key(brain) + "/surface")), ct);
}