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
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, string> GetUnitFiles() => ShippedUnits.Files(Alias);

    private static SynapseSource Shipped() => new(Alias, ShippedUnits.Files(Alias).Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
        .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal), null);

    // The feed of one poll: the posts with these ids, newest first.
    private static string Posts(params int[] ids) => JsonSerializer.Serialize(new { data = ids.Select(id => new { id = $"{id}", text = $"Post {id}" }).ToArray() });

    // One body per GET, the last one repeats.
    private static FakeSlots Feed(params string[] bodies)
    {
        var calls = 0;
        return new FakeSlots().Http("x", _ => bodies[Math.Min(Interlocked.Increment(ref calls), bodies.Length) - 1]);
    }

    private static ExpectState Holds(string field, string json) => new(Alias, field, JsonElement.Parse(json));

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
        Assert.Contains("✓ two polls of the same post count it once", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewPostAfterAPollAddsOne()
    {
        var transcript = await UnitScenario.Run(Shipped(), Feed(Posts(1), Posts(2, 1)),
            [new ScenarioTick(Start.AddMinutes(6)), new ScenarioTick(Start.AddMinutes(12)), Holds("Unseen", "2"), Holds("LastId", "\"2\"")], Ct);
        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        Assert.Contains("Post 2", transcript.Views.Last(v => v.Alias == Alias).View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]   // Review Focus 2: post 1 is gone from the feed at the second poll
    public async Task WhenTheCountedPostLeftTheFeedThePageIsCountedOnce()
    {
        var transcript = await UnitScenario.Run(Shipped(), Feed(Posts(1), Posts(5, 4, 3)),
            [new ScenarioTick(Start.AddMinutes(6)), new ScenarioTick(Start.AddMinutes(12)), Holds("Unseen", "4"),
             new ScenarioTick(Start.AddMinutes(18)), Holds("Unseen", "4"), Holds("LastId", "\"5\"")], Ct);
        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
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
