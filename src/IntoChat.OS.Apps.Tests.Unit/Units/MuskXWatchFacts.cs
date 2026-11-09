using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing.Synapses;
using IntoChat.OS.Modules.Apps.Debugger;
using ScenarioTick = DigitalBrain.Testing.Synapses.Tick;

namespace IntoChat.OS.Apps.Tests.Unit.Units;

public sealed class MuskXWatchFacts
{
    private const string Alias = "watch.x.elonmusk";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string MuskXWatchSource = """
        using System.Text.Json;
        using DigitalBrain;
        using DigitalBrain.Capabilities.Http;
        using DigitalBrain.Time;
        using DigitalBrain.UI;

        namespace Units.XWatch;

        [Signal("x.polled")]
        public sealed record Polled(int Fresh) : Signal;

        [State("watch.x.elonmusk")]
        public sealed record Watch(int Unseen = 0, string Latest = "")
        {
            public Watch() : this(0, "") { }
        }

        public static class XWatchUnit
        {
            [Synapse(typeof(Tick), "watch.x.elonmusk", Every = "every 6m")]
            public static async Task<Reaction<Watch>> Poll(Tick tick, Watch watch, [Slot] IHttpRead x, CancellationToken ct)
            {
                var feed = await x.GetJson("https://api.x.com/2/users/elonmusk/mentions", ct);
                var posts = feed.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
                var fresh = posts.ValueKind == JsonValueKind.Array ? posts.GetArrayLength() : 0;
                var latest = fresh > 0 && posts[0].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? watch.Latest
                    : watch.Latest;
                return Reaction<Watch>.Of(watch with { Unseen = watch.Unseen + fresh, Latest = latest }, new Polled(fresh));
            }

            [Synapse("x.seen", "watch.x.elonmusk")]
            public static Watch Seen(SignalEnvelope seen, Watch watch) => watch with { Unseen = 0 };

            [Synapse("watch.x.elonmusk")]
            public static View Show(Watch watch) => new("Musk X Watch", new Column(
            [
                new Heading("Musk X Watch", Subtitle: watch.Latest.Length == 0 ? "No mentions recorded yet." : watch.Latest),
                new Badge($"{watch.Unseen} unseen", Tone: watch.Unseen > 10 ? "red" : watch.Unseen > 0 ? "accent" : "neutral"),
                new Text(watch.Latest),
                new Button("mark-seen", "Mark seen", new Emit("watch.x.elonmusk", "x.seen")),
            ]) { Gap = 8 });
        }
        """;

    public const string MuskXWatchSpec = """
        # Musk X Watch

        Watches mentions of @elonmusk on X every 6 minutes, counts fresh mentions, updates the latest mention text, and maintains an unseen count until marked seen.

        - Every `6m`, reads mentions through the `x` slot, increments `Unseen` by the fresh count, updates `Latest`, and emits `x.polled`.
        - Reacts to `x.seen` signal by resetting `Unseen` to 0.
        - The view renders an unseen badge (`red` tone when > 10, `accent` otherwise), latest mention text, and a "Mark seen" button.
        """;

    public const string MuskXWatchScenarios = """
        [
          {
            "name": "a poll counts new mentions and updates latest text",
            "given": {
              "http": {
                "x": {
                  "https://api.x.com/2/users/elonmusk/mentions": {
                    "data": [
                      { "id": "1", "text": "Excited for Starship launch!" }
                    ]
                  }
                }
              }
            },
            "when": ["tick 6m"],
            "then": [
              "state watch.x.elonmusk Unseen = 1",
              "state watch.x.elonmusk Latest = \"Excited for Starship launch!\"",
              "signal x.polled"
            ]
          },
          {
            "name": "marking seen resets unseen count",
            "given": {
              "http": {
                "x": {
                  "https://api.x.com/2/users/elonmusk/mentions": {
                    "data": [
                      { "id": "1", "text": "Post 1" }
                    ]
                  }
                }
              }
            },
            "when": ["tick 6m", "publish x.seen {}"],
            "then": [
              "state watch.x.elonmusk Unseen = 0"
            ]
          }
        ]
        """;

    private static IReadOnlyDictionary<string, string> GetUnitFiles()
    {
        var shipped = ShippedUnits.Files(Alias);
        if (shipped.Count > 0)
        {
            return shipped;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XWatch.cs"] = MuskXWatchSource,
            ["spec.md"] = MuskXWatchSpec,
            ["scenarios.json"] = MuskXWatchScenarios
        };
    }

    [Fact]
    public async Task MuskXWatchUnitPassesAllBddScenarios()
    {
        var files = GetUnitFiles();
        Assert.True(files.ContainsKey("XWatch.cs") || files.Keys.Any(k => k.EndsWith(".cs", StringComparison.Ordinal)));
        Assert.True(files.ContainsKey("spec.md"));
        Assert.True(files.ContainsKey("scenarios.json"));

        var transcript = await ScenarioTranscripts.Run(Alias, files, null, Ct);
        Assert.False(transcript.Contains('✗', StringComparison.Ordinal), transcript);
        Assert.Contains("✓ a poll counts new mentions", transcript, StringComparison.Ordinal);
        Assert.Contains("✓ marking seen resets unseen count", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollIncrementsUnseenCounterAndUpdatesLatestMention()
    {
        var files = GetUnitFiles();
        var csFiles = files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var source = new SynapseSource(Alias, csFiles, null);

        var fakes = new FakeSlots();
        var postsJson = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "101", text = "Starbase tower catch confirmed" },
                new { id = "102", text = "FSD v13 rollout begins" },
                new { id = "103", text = "Grok 3 training cluster online" }
            }
        });
        fakes.Http("x", _ => postsJson);

        var tickTime = new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero);
        var transcript = await UnitScenario.Run(source, fakes, [new ScenarioTick(tickTime)], Ct);

        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        Assert.Single(transcript.Events, e => (e.Signal.Signal.GetType().GetCustomAttribute<SignalAttribute>()?.Alias ?? e.Signal.Signal.GetType().Name) == "x.polled");

        var view = Assert.Single(transcript.Views, v => v.Alias == Alias).View.GetRawText();
        Assert.Contains("3 unseen", view, StringComparison.Ordinal);
        Assert.Contains("Starbase tower catch confirmed", view, StringComparison.Ordinal);
        Assert.Contains("\"accent\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeenSignalResetsUnseenCounterToZero()
    {
        var files = GetUnitFiles();
        var csFiles = files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var source = new SynapseSource(Alias, csFiles, null);

        var fakes = new FakeSlots();
        fakes.Http("x", _ => JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "1", text = "Mention before seen" } }
        }));

        var tickTime = new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero);
        var transcript = await UnitScenario.Run(source, fakes,
        [
            new ScenarioTick(tickTime),
            new Publish("x.seen", JsonElement.Parse("{}"))
        ], Ct);

        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));

        var finalView = transcript.Views.Last(v => v.Alias == Alias).View.GetRawText();
        Assert.Contains("0 unseen", finalView, StringComparison.Ordinal);
        Assert.Contains("Mention before seen", finalView, StringComparison.Ordinal);
        Assert.Contains("\"neutral\"", finalView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadgeTurnsRedWhenUnseenMentionsExceedTen()
    {
        var files = GetUnitFiles();
        var csFiles = files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var source = new SynapseSource(Alias, csFiles, null);

        var fakes = new FakeSlots();
        var twelveMentions = JsonSerializer.Serialize(new
        {
            data = Enumerable.Range(1, 12).Select(i => new { id = $"{i}", text = $"Mention #{i}" }).ToArray()
        });
        fakes.Http("x", _ => twelveMentions);

        var tickTime = new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero);
        var transcript = await UnitScenario.Run(source, fakes, [new ScenarioTick(tickTime)], Ct);

        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));

        var view = Assert.Single(transcript.Views, v => v.Alias == Alias).View.GetRawText();
        Assert.Contains("12 unseen", view, StringComparison.Ordinal);
        Assert.Contains("\"red\"", view, StringComparison.Ordinal);
        Assert.Contains("mark-seen", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecAndNeedsDeclareHttpSlotSixMinuteScheduleAndView()
    {
        var files = GetUnitFiles();
        Assert.True(files.TryGetValue("spec.md", out var spec));
        Assert.Contains("6 minutes", spec, StringComparison.Ordinal);
        Assert.Contains("@elonmusk", spec, StringComparison.Ordinal);
        Assert.Contains("x.seen", spec, StringComparison.Ordinal);
    }
}
