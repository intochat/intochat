using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Scenarios;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Apps.UiKit;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests.Support;

namespace IntoChat.OS.Tests.Unit.Scenarios;

// Runs a scenario against the shipped UI Kit on TestOS: an Act is the command the edge /act sends (IScreen.Dispatch by key path, as
// the local person), a Read is the visible text of the target's subtree with every slot child's own view drawn in place.
internal sealed class KernelDriver(InstalledApp<UiKitApp> app) : IScenarioDriver
{
    private const int MaxSlotDepth = 4;
    private static readonly BrainId Brain = new("local");
    private string _lastRead = "";

    public string Name => "kernel";

    public static async Task<KernelDriver> Start(TestOS os)
    {
        os.Register<CheckBoxNeuron>().Register<ExpanderNeuron>().Register<TabStripNeuron>().Register<ListBoxNeuron>()
            .Register<LiveChartNeuron>().Register<QueryTableNeuron>().Register<SampleTableSource>();
        return new(await os.Install<UiKitApp>());
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
        var view = await app.Screen.Read() ?? throw new InvalidOperationException("The UI Kit has no view.");
        var subtree = target == "." ? view.View : await Find(view.View, target, 0)
            ?? throw new InvalidOperationException($"The UI Kit shows no node or slot '{target}'.");
        var texts = new List<string>();
        await Collect(subtree, texts, 0);
        _lastRead = JsonSerializer.Serialize(new { target, texts });
        return string.Join("\n", texts);
    }

    public Task<string?> Evidence(int stepIndex, CancellationToken ct) => Task.FromResult<string?>(_lastRead.Length == 0 ? null : _lastRead);

    // The node keyed `target`, or the slot whose key it is; a "slot/rest" path descends into that slot child's view.
    private async Task<JsonElement?> Find(JsonElement json, string target, int depth)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            if (SlotOf(json) is { } slot && depth < MaxSlotDepth)
            {
                if (slot.Key == target) { return json; }
                if (target.StartsWith(slot.Key + "/", StringComparison.Ordinal) && await ChildView(slot.Child) is { } child)
                {
                    return await Find(child, target[(slot.Key.Length + 1)..], depth + 1);
                }
                return null;
            }
            if (json.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() == target) { return json; }
            foreach (var property in json.EnumerateObject())
            {
                if (IsData(property.Name)) { continue; }
                if (await Find(property.Value, target, depth) is { } found) { return found; }
            }
        }
        else if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in json.EnumerateArray())
            {
                if (await Find(item, target, depth) is { } found) { return found; }
            }
        }
        return null;
    }

    // Every string a node shows, slot children drawn in place; keys, kinds, addresses and action payloads are not visible text.
    private async Task Collect(JsonElement json, List<string> texts, int depth)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                if (SlotOf(json) is { } slot)
                {
                    // The parent's props for the slot (a chart title, a label, a placeholder) are drawn with the child's view.
                    if (json.TryGetProperty("props", out var props)) { await Collect(props, texts, depth); }
                    if (depth < MaxSlotDepth && await ChildView(slot.Child) is { } child) { await Collect(child, texts, depth + 1); }
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

    private static (string Key, NeuronId Child)? SlotOf(JsonElement json)
        => json.TryGetProperty("refKind", out var refKind) && refKind.ValueKind == JsonValueKind.String && refKind.GetString() == "slot"
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
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly).Register<RunNeuron>();
        var driver = await KernelDriver.Start(os);
        var run = os.Get<IRun>(new NeuronId(new BrainId("local"), "test.run", name));

        RunVerdict verdict;
        using (os.Edge.Enter(os.Edge.Person("local", "local", new BrainId("local"))))
        {
            verdict = await ScenarioRunner.Run(scenario, driver, run, ct);
        }

        Assert.True(verdict.Passed, $"{name} failed at step {verdict.FailedStep} ({(verdict.FailedStep is { } i ? JsonSerializer.Serialize(scenario.Steps[i]) : "-")}): {verdict.Reason}");
    }
}
