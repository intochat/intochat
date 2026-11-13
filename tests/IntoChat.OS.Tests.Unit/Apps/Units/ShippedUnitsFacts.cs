using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing.Synapses;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Debugger;

namespace IntoChat.OS.Apps.Tests.Unit.Units;

public sealed class ShippedUnitsFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, string, int> Catalog => new()
    {
        { "watch.x.elonmusk", "XWatch.cs", "Musk X Watch", 4 },
        { "triage.github.issues", "GithubIssueTriager.cs", "GitHub Issue Triager", 2 },
        { "digest.hn.top", "HnDigest.cs", "Hacker News Scheduled Digest", 1 },
        { "organizer.work.notes", "WorkNotesOrganizer.cs", "Workspace Markdown Organizer", 2 },
        { "fx.rates.monitor", "FxRatesMonitor.cs", "Live Currency & Crypto Monitor", 2 },
        { "circleroom", "CircleRoom.cs", "Circle room", 2 },
        { "watch.web", "WebWatch.cs", "Web watch", 4 },
        { "brief.daily", "DailyBrief.cs", "Daily brief", 2 },
        { "data.csv", "CsvExplorer.cs", "CSV explorer", 2 },
        { "relay.webhook", "WebhookRelay.cs", "Webhook relay", 2 },
    };

    [Fact]
    public void AllAndLegacyListEveryShippedUnitAliasInOrder()
    {
        Assert.Equal(["watch.x.elonmusk", "triage.github.issues", "digest.hn.top", "organizer.work.notes", "fx.rates.monitor"], ShippedUnits.All);
        Assert.Equal(["circleroom", "watch.web", "brief.daily", "data.csv", "relay.webhook"], ShippedUnits.Legacy);
    }

    [Theory]
    [MemberData(nameof(Catalog))]
    public async Task EveryShippedUnitEmbedsFilesTitleAndPassesAllScenarios(string alias, string codeFile, string expectedTitle, int minScenarios)
    {
        var files = ShippedUnits.Files(alias);
        Assert.Equal([codeFile, "scenarios.json", "spec.md"], files.Keys.Order(StringComparer.Ordinal));
        Assert.All(files.Values, content => Assert.False(string.IsNullOrWhiteSpace(content)));

        var title = ShippedUnits.Title(alias);
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.NotEqual(alias, title);
        Assert.Equal(expectedTitle, title);

        var transcript = await ScenarioTranscripts.Run(alias, files, null, Ct);
        Assert.False(transcript.Contains('✗', StringComparison.Ordinal), transcript);
        var passed = transcript.Split('\n').Count(line => line.StartsWith('✓'));
        Assert.True(passed >= minScenarios, $"Expected at least {minScenarios} passing scenarios for {alias}, got {passed}:\n{transcript}");
    }

    [Fact]
    public async Task WebWatchBadgeSwitchesToErrorToneAboveTenUnseen()
    {
        var files = ShippedUnits.Files("watch.web");
        var source = new SynapseSource("watch.web", files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal), null);
        var fakes = new FakeSlots();
        var elevenItems = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(1, 11).Select(i => new { title = $"Item {i}" }).ToArray(),
        });
        fakes.Http("http", _ => elevenItems);

        var transcript = await UnitScenario.Run(source, fakes, [new DigitalBrain.Testing.Synapses.Tick(new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero))], Ct);
        Assert.True(transcript.Passed, string.Join('\n', transcript.Failures));
        var view = Assert.Single(transcript.Views, v => v.Alias == "watch.web").View.GetRawText();
        Assert.Contains("11 unseen", view, StringComparison.Ordinal);
        Assert.Contains("\"error\"", view, StringComparison.Ordinal);
        Assert.Contains("mark-seen", view, StringComparison.Ordinal);
    }
}
