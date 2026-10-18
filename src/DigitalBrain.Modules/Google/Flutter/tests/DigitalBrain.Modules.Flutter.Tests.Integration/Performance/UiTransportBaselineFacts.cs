using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Views;
using DigitalBrain.Modules.Flutter.Tests.Integration.Contracts;

namespace DigitalBrain.Modules.Flutter.Tests.Integration.Performance;

// W2 PR 0 (ui-contract 5.4): today's cost of the shell's UI transports, so every later PR reports before/after.
// The scenarios replay what the shell does over T3/T4 (workspace_app.dart, ui_client.dart) against the shared
// in-process host: first paint reads the root view and then one GET /apps/node per UiChildRef, sequentially; a
// click posts /apps/event and reads the button again (the nudge that triggers the re-read is not timed); the root
// view's 2 s poll is one GET of the view. Request counts and bytes are deterministic and asserted; times are
// recorded, never asserted. CaptureUiTransportBaseline (run explicitly) rewrites ui-transport-baseline.json.
public sealed class UiTransportBaselineFacts(FlutterHost host)
{
    internal const string BaselineFile = "ui-transport-baseline.json";
    private const int Buttons = 10, Texts = 10, PollsPerMinute = 30;

    private sealed record Scenario(string Name, int Requests, long Bytes, double[] Milliseconds);

    [Fact]
    public async Task TodaysTransportCostIsMeasured()
    {
        var scenarios = await Measure(warmup: 2, iterations: 10, TestContext.Current.CancellationToken);
        var baseline = UiContractFiles.Golden(Path.Combine("..", "Performance", BaselineFile))["scenarios"]!.AsObject();

        foreach (var scenario in scenarios)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{scenario.Name}: {scenario.Requests} requests, {scenario.Bytes} bytes, p50 {Percentile(scenario.Milliseconds, 50):F2} ms, p95 {Percentile(scenario.Milliseconds, 95):F2} ms");
            var recorded = baseline[scenario.Name]!;
            Assert.Equal(recorded["requests"]!.GetValue<int>(), scenario.Requests);
            Assert.Equal(recorded["bytes"]!.GetValue<long>(), scenario.Bytes);
        }
        Assert.Equal(baseline.Select(entry => entry.Key).Order(StringComparer.Ordinal), scenarios.Select(scenario => scenario.Name).Order(StringComparer.Ordinal));
    }

    [Fact(Explicit = true)]
    public async Task CaptureUiTransportBaseline()
    {
        var scenarios = await Measure(warmup: 5, iterations: 50, TestContext.Current.CancellationToken);
        var poll = scenarios.Single(scenario => scenario.Name == "root-view-poll-2s");
        var entries = new JsonObject();
        foreach (var scenario in scenarios)
        {
            entries[scenario.Name] = new JsonObject
            {
                ["requests"] = scenario.Requests,
                ["bytes"] = scenario.Bytes,
                ["p50Ms"] = Math.Round(Percentile(scenario.Milliseconds, 50), 2),
                ["p95Ms"] = Math.Round(Percentile(scenario.Milliseconds, 95), 2),
                ["meanMs"] = Math.Round(scenario.Milliseconds.Average(), 2),
            };
        }
        entries[poll.Name]!["perMinute"] = new JsonObject
        {
            ["requests"] = PollsPerMinute * poll.Requests,
            ["bytes"] = PollsPerMinute * poll.Bytes,
            ["p50Ms"] = Math.Round(PollsPerMinute * Percentile(poll.Milliseconds, 50), 2),
        };
        UiContractFiles.Write(Path.Combine("Performance", BaselineFile), new JsonObject
        {
            ["measured"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["method"] = "UiTransportBaselineFacts.CaptureUiTransportBaseline on the shared in-process FlutterHost (backend only, no network hop). "
                + $"One view whose root column holds a title and {Buttons} button and {Texts} text UiChildRefs. 5 warm-up runs, then 50 timed runs per scenario, "
                + "Stopwatch around the shell's HTTP calls. first-paint: GET view + one sequential GET /apps/node per reference. click-round-trip: POST /apps/event "
                + "for a button + GET of that button (nudge latency excluded). root-view-poll-2s: one GET of the view; perMinute = 30 polls. Bytes are response bodies "
                + "(click digits excluded). Times compare only against reruns of this fact on the same machine: "
                + $"{typeof(UiTransportBaselineFacts).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration} build, {Environment.ProcessorCount} logical cores, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}.",
            ["scenarios"] = entries,
        });
    }

    private async Task<Scenario[]> Measure(int warmup, int iterations, CancellationToken ct)
    {
        using var person = await host.PersonAsync(ct: ct);
        using var caller = person.AsCaller();
        var app = person.BrainId + "/apps/w2-baseline";
        var children = new List<UiNode>();
        for (var i = 0; i < Buttons; i++)
        {
            await host.Brain.Get<IButton>($"{app}/button-{i}").Set($"Button {i}", "go");
            children.Add(Ui.Reference($"button-{i}", new(UIVocabulary.ButtonType, $"{app}/button-{i}")));
        }
        for (var i = 0; i < Texts; i++)
        {
            await host.Brain.Get<IText>($"{app}/text-{i}").Set($"Paragraph {i} of the baseline screen.");
            children.Add(Ui.Reference($"text-{i}", new(UIVocabulary.TextType, $"{app}/text-{i}")));
        }
        var view = app + "/view";
        await host.Brain.Get<IView>(view).Publish(new(1, 1, Ui.Column("root", [Ui.Text("title", "Baseline"), .. children])));
        var http = person.Http;
        var apps = $"/brains/{person.BrainId}/apps";
        string Node(string kind, string name) => $"{apps}/node?kind={kind}&name={Uri.EscapeDataString(name)}";

        async Task<(int Requests, long Bytes)> FirstPaint()
        {
            var root = await http.GetStringAsync(Node(UIVocabulary.ViewType, view), ct);
            var (requests, bytes) = (1, (long)root.Length);
            foreach (var reference in JsonNode.Parse(root)!["root"]!["children"]!.AsArray().Select(node => node!["reference"]).OfType<JsonNode>())
            {
                var body = await http.GetStringAsync(Node(reference["kind"]!.GetValue<string>(), reference["name"]!.GetValue<string>()), ct);
                (requests, bytes) = (requests + 1, bytes + body.Length);
            }
            return (requests, bytes);
        }

        async Task<(int Requests, long Bytes)> Click()
        {
            using var posted = await http.PostAsJsonAsync($"{apps}/event", new { kind = UIVocabulary.ButtonType, name = $"{app}/button-0", action = "go" }, ct);
            posted.EnsureSuccessStatusCode();
            var accepted = await posted.Content.ReadAsStringAsync(ct);
            var state = await http.GetStringAsync(Node(UIVocabulary.ButtonType, $"{app}/button-0"), ct);
            return (2, accepted.Length + state.Length - Digits(state));
        }

        async Task<(int Requests, long Bytes)> Poll() => (1, (await http.GetStringAsync(Node(UIVocabulary.ViewType, view), ct)).Length);

        return [await Run("first-paint", FirstPaint), await Run("click-round-trip", Click), await Run("root-view-poll-2s", Poll)];

        async Task<Scenario> Run(string name, Func<Task<(int Requests, long Bytes)>> scenario)
        {
            for (var i = 0; i < warmup; i++) { await scenario(); }
            var times = new double[iterations];
            (int Requests, long Bytes) shape = default;
            for (var i = 0; i < iterations; i++)
            {
                var watch = Stopwatch.StartNew();
                shape = await scenario();
                times[i] = watch.Elapsed.TotalMilliseconds;
            }
            return new(name, shape.Requests, shape.Bytes, times);
        }
    }

    // The click count and version grow with every run; their digits are not part of the transport's shape.
    private static int Digits(string state)
    {
        var json = JsonNode.Parse(state)!;
        return json["clickCount"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture).Length
            + json["version"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture).Length;
    }

    private static double Percentile(double[] values, int percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
