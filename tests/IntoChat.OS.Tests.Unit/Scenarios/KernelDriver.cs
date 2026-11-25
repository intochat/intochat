using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Scenarios;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests.Support;

namespace IntoChat.OS.Tests.Unit.Scenarios;

// Runs a scenario against the shipped UI Gallery on TestOS: an Act is the command the edge /act sends (IScreen.Dispatch by key path, as
// the local person), a Read is the visible text of the target's subtree with every child's own view drawn in place.
internal sealed class KernelDriver(InstalledApp<GalleryApp> app) : IScenarioDriver
{
    private const int MaxChildDepth = 4;
    private static readonly BrainId Brain = new("local");
    private string _lastRead = "";

    public string Name => "kernel";

    public static async Task<KernelDriver> Start(TestOS os)
    {
        os.Register<CheckBoxNeuron>().Register<ExpanderNeuron>().Register<TabStripNeuron>().Register<ListBoxNeuron>()
            .Register<LiveChartNeuron>().Register<QueryTableNeuron>().Register<SampleTableSource>()
            .Register<KanbanNeuron>().Register<StepperNeuron>().Register<TreeNeuron>().Register<SliderNeuron>();
        return new(await os.Install<GalleryApp>());
    }

    public async Task Act(Act step, CancellationToken ct)
    {
        using (app.OS.Edge.Enter(app.OS.Edge.Person("local", "local", Brain)))
        {
            await app.Screen.Dispatch(new Command(step.Target, step.Signal, step.Args ?? JsonSerializer.SerializeToElement(new { })));
        }
        await app.OS.Idle();
    }

    public async Task<string> Read(string target, CancellationToken ct)
    {
        using var person = app.OS.Edge.Enter(app.OS.Edge.Person("local", "local", Brain));
        var view = await app.Screen.Read() ?? throw new InvalidOperationException("The Gallery has no view.");
        var subtree = target == "." ? view.View : await Find(view.View, target)
            ?? throw new InvalidOperationException($"The Gallery shows no node or child '{target}'.");
        var texts = new List<string>();
        await Collect(subtree, texts, 0);
        _lastRead = JsonSerializer.Serialize(new { target, texts });
        return string.Join("\n", texts);
    }

    // The page as the Flutter driver replays it: the top-level view and the root view of every child it shows (nested children
    // too, to the same depth a read draws), keyed by the child's key path, as GET /apps/{name}/children/{path} serves them: child refs name
    // their key, never the child's neuron id.
    public async Task<JsonElement> Snapshot()
    {
        using var person = app.OS.Edge.Enter(app.OS.Edge.Person("local", "local", Brain));
        var view = await app.Screen.Read() ?? throw new InvalidOperationException("The Gallery has no view.");
        var children = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        await Children(view.View, "", children, 0);
        return JsonSerializer.SerializeToElement(new { view = IntoChat.OS.Modules.Flutter.ChildKeys.Screen(view.View), children });
    }

    private async Task Children(JsonElement json, string path, SortedDictionary<string, JsonElement> children, int depth)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                if (ChildOf(json) is { } reference)
                {
                    var childPath = path.Length == 0 ? reference.Key : path + "/" + reference.Key;
                    if (depth < MaxChildDepth && !children.ContainsKey(childPath) && await ChildView(reference.Child) is { } child)
                    {
                        children[childPath] = JsonSerializer.SerializeToElement(new { root = IntoChat.OS.Modules.Flutter.ChildKeys.Hide(child), acts = DigitalBrain.UI.View.ActRoute });
                        await Children(child, childPath, children, depth + 1);
                    }
                    return;
                }
                foreach (var property in json.EnumerateObject()) { await Children(property.Value, path, children, depth); }
                break;
            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray()) { await Children(item, path, children, depth); }
                break;
        }
    }

    public Task<string?> Evidence(int stepIndex, CancellationToken ct) => Task.FromResult<string?>(_lastRead.Length == 0 ? null : _lastRead);

    // Resolves a key path the way the server routes it: child keys first. While the leading segment names a child of the current view,
    // descend into that child's view; the last segment is a child of the current view, else the node with that key in it.
    private async Task<JsonElement?> Find(JsonElement view, string target)
    {
        var current = view;
        var rest = target;
        for (var depth = 0; depth < MaxChildDepth && rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0; depth++)
        {
            if (ChildRef(current, rest[..slash]) is not { } reference || await ChildView(reference.Child) is not { } child) { break; }
            current = child;
            rest = rest[(slash + 1)..];
        }
        return ChildRef(current, rest)?.Node ?? Keyed(current, rest);
    }

    // A child of this view (not of its children's children, which are only references here) with that key.
    private static (JsonElement Node, NeuronId Child)? ChildRef(JsonElement json, string key)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            if (ChildOf(json) is { } reference) { return reference.Key == key ? (json, reference.Child) : null; }
            foreach (var property in json.EnumerateObject())
            {
                if (!IsData(property.Name) && ChildRef(property.Value, key) is { } found) { return found; }
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray()) { if (ChildRef(item, key) is { } found) { return found; } }
        }
        return null;
    }

    private static JsonElement? Keyed(JsonElement json, string key)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            if (ChildOf(json) is not null) { return null; }
            if (json.TryGetProperty("key", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == key) { return json; }
            foreach (var property in json.EnumerateObject())
            {
                if (!IsData(property.Name) && Keyed(property.Value, key) is { } found) { return found; }
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray()) { if (Keyed(item, key) is { } found) { return found; } }
        }
        return null;
    }

    // Every string a node shows, children drawn in place; keys, kinds, addresses and action payloads are not visible text.
    private async Task Collect(JsonElement json, List<string> texts, int depth)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                if (ChildOf(json) is { } reference)
                {
                    // The parent's props for the child (a chart title, a label, a placeholder) are drawn with the child's view.
                    if (json.TryGetProperty("props", out var props)) { await Collect(props, texts, depth); }
                    if (depth < MaxChildDepth && await ChildView(reference.Child) is { } child) { await Collect(child, texts, depth + 1); }
                    return;
                }
                foreach (var property in json.EnumerateObject())
                {
                    if (IsData(property.Name) || property.Name is "key" or "kind" or "refKind" or "neuronUri" or "icon" or "tone" or "id" or "automationName") { continue; }
                    await Collect(property.Value, texts, depth);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray()) { await Collect(item, texts, depth); }
                break;
            case JsonValueKind.String:
                if (json.GetString() is { Length: > 0 } text) { texts.Add(text); }
                break;
        }
    }

    private async Task<JsonElement?> ChildView(NeuronId child)
        => await app.OS.Get<IScreen>(child).Read() is { } view && view.View.TryGetProperty("root", out var root) ? root : null;

    private static bool IsData(string name) => name is "command" or "emit" or "args" or "intent" or "resolve" or "onSubmit" or "props";

    private static (string Key, NeuronId Child)? ChildOf(JsonElement json)
        => json.TryGetProperty("refKind", out var refKind) && refKind.ValueKind == JsonValueKind.String && refKind.GetString() == NeuronRef.ChildKind
            && json.TryGetProperty("neuronUri", out var uri) && uri.ValueKind == JsonValueKind.String
            && NeuronId.TryParse(uri.GetString(), out var child) && child.Key.LastIndexOf('/') is var slash and > 0
                ? (child.Key[(slash + 1)..], child)
                : null;
}

public sealed class KernelScenarioFacts
{
    public static TheoryData<string> Names => [.. ShowcaseScenarios.All.Select(scenario => scenario.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_showcase_scenario_passes_on_the_kernel(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = ShowcaseScenarios.All.Single(item => item.Name == name);
        await using var os = TestOS.Local().RegisterCatalog().Register<RunNeuron>();
        var driver = await KernelDriver.Start(os);
        var run = os.Get<IRun>(new NeuronId(new BrainId("local"), "test.run", name));

        RunVerdict verdict;
        using (os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local"))))
        {
            verdict = await ScenarioRunner.Run(scenario, driver, run, ct);
        }

        Assert.True(verdict.Passed, $"{name} failed at step {verdict.FailedStep} ({(verdict.FailedStep is { } i ? JsonSerializer.Serialize(scenario.Steps[i]) : "-")}): {verdict.Reason}");
    }

    // Every scenario must bite: without its nav select (the Gallery opens on the first page) it fails.
    public static TheoryData<string> NotFirst => [.. ShowcaseScenarios.All.Select(scenario => scenario.Name).Where(name => name != GalleryPages.First)];

    [Theory]
    [MemberData(nameof(NotFirst))]
    public async Task A_scenario_fails_when_its_page_is_not_opened(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = ShowcaseScenarios.All.Single(item => item.Name == name);
        await using var os = TestOS.Local().RegisterCatalog().Register<RunNeuron>();
        var driver = await KernelDriver.Start(os);

        RunVerdict verdict;
        using (os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local"))))
        {
            verdict = await ScenarioRunner.Run(scenario with { Steps = [.. scenario.Steps.Skip(1)] }, driver, os.Get<IRun>(new NeuronId(new BrainId("local"), "test.run", name)), ct);
        }

        Assert.IsType<Act>(scenario.Steps[0]);
        Assert.False(verdict.Passed, $"{name} passed without opening its page.");
    }

    [Fact]
    public async Task Selecting_a_page_changes_what_its_key_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local().RegisterCatalog();
        var driver = await KernelDriver.Start(os);

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.Read("checkBox", ct));
        await driver.Act(new Act(GalleryPages.Nav, "control.select", JsonSerializer.SerializeToElement(new { id = "checkBox" })), ct);

        Assert.Contains("Remember me", await driver.Read("checkBox", ct), StringComparison.Ordinal);
    }
}
