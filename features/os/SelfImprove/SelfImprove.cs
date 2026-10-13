using DigitalBrain.Apps;
using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Microsoft.Aspire;

// Shared by every synapse: the app's key, its brain, the change-set shape and the list rendering.
public static class SelfImprove
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Scope(DigitalBrainConnection brain) => brain.Setting("Brain")!;

    public static bool Ended(CandidateStage stage) => stage is CandidateStage.Promoted or CandidateStage.Discarded or CandidateStage.Failed;

    // Every field may be missing from the JSON; Parse checks them all.
    public sealed record ChangeSet(string? Branch, string? Summary, Dictionary<string, string?>? Files, PlanJson? Plan);
    public sealed record PlanJson(CallJson?[]? Calls, string?[]? Spans, string? Budget);
    public sealed record CallJson(string? Contract, string? Key, string? Method, string? Arguments, string? Expected);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A malformed change set is refused here with its reason (an ArgumentException), before anything is built.
    public static CandidateProposal Parse(string input)
    {
        var change = JsonSerializer.Deserialize<ChangeSet>(input, Json) ?? throw new ArgumentException("A change set is a JSON object.");
        if (string.IsNullOrWhiteSpace(change.Branch)) { throw new ArgumentException("A change set names its branch."); }
        if (string.IsNullOrWhiteSpace(change.Summary)) { throw new ArgumentException("A change set has a summary."); }
        if (change.Files is not { Count: > 0 } files || files.Any(file => string.IsNullOrWhiteSpace(file.Key) || file.Value is null))
        { throw new ArgumentException("A change set carries its files: an object of repo path to content, at least one."); }
        if (change.Plan is not { Calls: { } calls, Spans: { } spans } plan) { throw new ArgumentException("A change set carries its verification plan: calls, spans and budget."); }
        if (!TimeSpan.TryParse(plan.Budget, System.Globalization.CultureInfo.InvariantCulture, out var budget) || budget <= TimeSpan.Zero)
        { throw new ArgumentException($"The plan's budget is a positive time span such as \"00:20:00\", not \"{plan.Budget}\"."); }
        var expectations = new List<NeuronExpectation>();
        foreach (var call in calls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Contract) || string.IsNullOrWhiteSpace(call.Key) || string.IsNullOrWhiteSpace(call.Method) || call.Expected is null)
            { throw new ArgumentException("Each plan call names its contract, key, method and expected answer (\"*\" for any)."); }
            if (!IsJsonArray(call.Arguments)) { throw new ArgumentException($"A plan call's arguments are one JSON array string; {call.Method} has {call.Arguments ?? "none"}."); }
            expectations.Add(new(call.Contract, call.Key, call.Method, call.Arguments!, call.Expected));
        }
        if (spans.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("Each plan span is a span name."); }
        return new(change.Branch, change.Summary, files.ToDictionary(file => file.Key, file => file.Value!), new VerificationPlan([.. expectations], [.. spans.Select(span => span!)], budget));
    }

    private static bool IsJsonArray(string? json)
    {
        if (json is null) { return false; }
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    // The list is rebuilt from the kernel's candidate instances and the cluster every time, never patched.
    // Newest first, so the candidate just proposed heads the list.
    public static async Task RenderListAsync(DigitalBrainConnection brain)
    {
        var instances = new List<(DateTimeOffset Created, string Key)>();
        await foreach (var instance in brain.Kernel.Instances<ICandidate>(brain.Stopping)) { instances.Add((instance.Created, instance.Key)); }
        var lines = new List<string>();
        foreach (var (_, key) in instances.OrderByDescending(instance => instance.Created))
        {
            var candidate = await brain.Get<ICandidate>(key).Read();
            lines.Add($"- `{candidate.Sha}` — {candidate.Stage} — {candidate.Summary}");
        }
        var cluster = await brain.Get<IDeployment>(IDeployment.Key(Scope(brain))).Read();
        // The deployment names a build only once a candidate was promoted onto it; before that it runs the image it booted.
        var build = cluster.CurrentSha.Length == 0 ? "the booted image (nothing promoted yet)" : $"`{cluster.CurrentSha}`";
        var live = $"Live: {build} — {cluster.Stage}";
        await brain.Get<IText>(Key(brain) + "/revisions").Set(lines.Count == 0 ? "_No candidates yet._\n\n" + live : string.Join("\n", lines) + "\n\n" + live);
    }

    public static async Task ComposeAsync(DigitalBrainConnection brain)
    {
        var appKey = Key(brain);
        string Name(string part) => appKey + "/" + part;
        var main = brain.Get<ILayout>(Name("main"));
        await main.Set(new LayoutDefinition("column", [new UiChildRef(UIVocabulary.TextType, Name("revisions")), new UiChildRef(UIVocabulary.TextType, Name("report"))], Extents: [0, 160]), (await main.Read()).Revision);
        await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main")));
    }

}
