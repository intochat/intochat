using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Revisions;

// Shared by every synapse: the app's key, its brain, the change-set shape and the list rendering.
public static class SelfImprove
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public sealed record ChangeSet(string Branch, string Summary, Dictionary<string, string> Files, PlanJson Plan);
    public sealed record PlanJson(CallJson[] Calls, string[] HealthyResources, string[] Spans, string Budget, string? AspireKey);
    public sealed record CallJson(string Contract, string Key, string Method, string Arguments, string Expected);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RevisionProposal Parse(string input)
    {
        var change = JsonSerializer.Deserialize<ChangeSet>(input, Json) ?? throw new ArgumentException("A change set is a JSON object.");
        var plan = new VerificationPlan(
            [.. change.Plan.Calls.Select(call => new NeuronExpectation(call.Contract, call.Key, call.Method, call.Arguments, call.Expected))],
            change.Plan.HealthyResources, change.Plan.Spans, TimeSpan.Parse(change.Plan.Budget, System.Globalization.CultureInfo.InvariantCulture),
            change.Plan.AspireKey ?? "DigitalBrain.OS");
        return new(change.Branch, change.Summary, change.Files, plan);
    }

    // The list is rebuilt from the kernel's revision instances every time, never patched.
    public static async Task RenderListAsync(DigitalBrainConnection brain)
    {
        var lines = new List<string>();
        await foreach (var instance in brain.Kernel.Instances<IRevision>(brain.Stopping))
        {
            var revision = await brain.Get<IRevision>(instance.Key).Read();
            lines.Add($"- `{revision.Sha}` — {revision.Stage} — {revision.Summary}");
        }
        await brain.Get<IText>(Key(brain) + "/revisions").Set(lines.Count == 0 ? "_No revisions yet._" : string.Join("\n", lines));
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