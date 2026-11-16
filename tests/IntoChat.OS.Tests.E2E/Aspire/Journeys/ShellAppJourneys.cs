using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DigitalBrain.Testing.Views;
using DigitalBrain.UI;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Journeys;

[Collection(OsAppHostCollection.Name)]
public sealed class ShellAppJourneys(OsAppHostFixture host)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly IReadOnlyDictionary<string, Journey> JourneysByApp =
        new Journey[]
        {
            new("settings", "open", "{}", "open.json"),
            new("gallery", "open", "{}", "open.json"),
            new("creator", "open", "{}", "open.json"),
            new("ino", "open", "{}", "open.json"),
            new("desktop", "open", "{}", "open.json"),
            new("data", "open", "{}", "open.json"),
            new("markdraw", "open", "{}", "open.json"),
            new("photocraft", "open", "{}", "open.json"),
            new("filmcraft", "open", "{}", "open.json"),
        }.ToDictionary(j => j.App, StringComparer.Ordinal);

    public static TheoryData<string> Apps =>
    [
        "settings",
        "gallery",
        "creator",
        "ino",
        "desktop",
        "data",
        "markdraw",
        "photocraft",
        "filmcraft",
    ];

    [Theory]
    [MemberData(nameof(Apps))]
    public async Task ShellAppAnswersItsOpenScenarioOverHttp(string app)
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);

        var journey = JourneysByApp[app];
        var fixturePath = ResolveFixturePath(journey.App, journey.FixtureFile);
        Assert.True(File.Exists(fixturePath), $"Missing view fixture '{fixturePath}' for app '{journey.App}'.");

        var expectedFixture = JsonSerializer.Deserialize<ViewFixture>(
            await File.ReadAllTextAsync(fixturePath, Encoding.UTF8, ct),
            JsonOptions);
        Assert.NotNull(expectedFixture?.Main);

        if (string.Equals(journey.App, "creator", StringComparison.Ordinal))
        {
            var inoBaseline = await ReadGenerationAsync("ino", ct);
            using var inoOpen = await host.Os.PostAsJsonAsync(
                $"/brains/{host.Scope}/apps/ino/request",
                new { operation = "open", input = "{}" },
                ct);
            Assert.Equal(HttpStatusCode.Accepted, inoOpen.StatusCode);

            var inoFixture = JsonSerializer.Deserialize<ViewFixture>(
                await File.ReadAllTextAsync(ResolveFixturePath("ino", "open.json"), Encoding.UTF8, ct),
                JsonOptions);
            _ = await PollMatchingOrStableViewAsync("ino", inoFixture!.Main!, inoBaseline, updatingFixtures: false, ct);
        }

        var baselineGeneration = await ReadGenerationAsync(journey.App, ct);

        using var postResponse = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/{journey.App}/request",
            new { operation = journey.Operation, input = journey.Argument },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var updatingFixtures = string.Equals(Environment.GetEnvironmentVariable("INTOCHAT_UPDATE_VIEW_FIXTURES"), "1", StringComparison.Ordinal);
        var actualView = await PollMatchingOrStableViewAsync(journey.App, expectedFixture!.Main!, baselineGeneration, updatingFixtures, ct);
        if (updatingFixtures && ViewComparison.Diff(expectedFixture.Main!, actualView) is not null)
        {
            var updated = expectedFixture with { Main = actualView };
            var json = JsonSerializer.Serialize(updated, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
            await File.WriteAllTextAsync(fixturePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            return;
        }

        var diff = ViewComparison.Diff(expectedFixture.Main!, actualView);
        Assert.True(diff is null, diff);
    }

    [Fact]
    public async Task AJourneyFailsOnAViewDrift()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);

        var journey = JourneysByApp["settings"];
        var fixturePath = ResolveFixturePath(journey.App, journey.FixtureFile);
        var expectedFixture = JsonSerializer.Deserialize<ViewFixture>(
            await File.ReadAllTextAsync(fixturePath, Encoding.UTF8, ct),
            JsonOptions);
        Assert.NotNull(expectedFixture?.Main);

        var baselineGeneration = await ReadGenerationAsync(journey.App, ct);

        using var postResponse = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/{journey.App}/request",
            new { operation = journey.Operation, input = journey.Argument },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var actualView = await PollMatchingOrStableViewAsync(journey.App, expectedFixture!.Main!, baselineGeneration, updatingFixtures: false, ct);
        var driftedExpected = MutateFirstTextNode(expectedFixture.Main!, "DRIFTED_TEXT_NODE");

        var diff = ViewComparison.Diff(driftedExpected, actualView);
        Assert.NotNull(diff);
        Assert.Contains("/root/children/0/children/1/children/2/value", diff, StringComparison.Ordinal);
        Assert.Contains("DRIFTED_TEXT_NODE", diff, StringComparison.Ordinal);
    }

    private static View MutateFirstTextNode(View source, string newText)
    {
        var (mutatedRoot, changed) = MutateNode(source.Root, newText);
        Assert.True(changed, "Expected to find and mutate a Text node in the view.");
        return source with { Root = mutatedRoot };
    }

    private static (Node Node, bool Changed) MutateNode(Node node, string newText)
    {
        switch (node)
        {
            case Text text:
                return (text with { Value = newText }, true);

            case Column col:
                {
                    var children = col.Children.ToArray();
                    for (var i = 0; i < children.Length; i++)
                    {
                        var (next, changed) = MutateNode(children[i], newText);
                        if (changed)
                        {
                            children[i] = next;
                            return (col with { Children = children }, true);
                        }
                    }
                    return (col, false);
                }

            case Row row:
                {
                    var children = row.Children.ToArray();
                    for (var i = 0; i < children.Length; i++)
                    {
                        var (next, changed) = MutateNode(children[i], newText);
                        if (changed)
                        {
                            children[i] = next;
                            return (row with { Children = children }, true);
                        }
                    }
                    return (row, false);
                }

            case Surface surface:
                {
                    var children = surface.Children.ToArray();
                    for (var i = 0; i < children.Length; i++)
                    {
                        var (next, changed) = MutateNode(children[i], newText);
                        if (changed)
                        {
                            children[i] = next;
                            return (surface with { Children = children }, true);
                        }
                    }
                    return (surface, false);
                }

            default:
                return (node, false);
        }
    }

    private async Task<long> ReadGenerationAsync(string app, CancellationToken ct)
    {
        var name = $"{host.Scope}/apps/{app}/view";
        using var response = await host.Os.GetAsync($"/brains/{host.Scope}/apps/node?kind=view&name={Uri.EscapeDataString(name)}", ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return 0;
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("generation", out var genElem)
            && genElem.TryGetInt64(out var gen)
            ? gen
            : 0;
    }

    internal async Task<View> PollMatchingOrStableViewAsync(string app, View expected, long baselineGeneration, bool updatingFixtures, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var name = $"{host.Scope}/apps/{app}/view";
        string? lastViewRaw = null;
        var unchangedPolls = 0;

        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await host.Os.GetAsync($"/brains/{host.Scope}/apps/node?kind=view&name={Uri.EscapeDataString(name)}", deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var raw = await response.Content.ReadAsStringAsync(deadline.Token);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("view", out var viewElem)
                        && viewElem.ValueKind == JsonValueKind.Object)
                    {
                        var generation = doc.RootElement.TryGetProperty("generation", out var genElem) && genElem.TryGetInt64(out var gen) ? gen : 0;
                        var viewRaw = viewElem.GetRawText();
                        if (string.Equals(viewRaw, lastViewRaw, StringComparison.Ordinal))
                        {
                            unchangedPolls++;
                        }
                        else
                        {
                            lastViewRaw = viewRaw;
                            unchangedPolls = 0;
                        }

                        var deserialized = JsonSerializer.Deserialize<View>(viewRaw, JsonOptions);
                        if (deserialized is not null)
                        {
                            if (!updatingFixtures && ViewComparison.Diff(expected, deserialized) is null)
                            {
                                return deserialized;
                            }

                            var settledAfterChange = generation > baselineGeneration && unchangedPolls >= (updatingFixtures ? 10 : 5);
                            if (settledAfterChange || unchangedPolls >= 60)
                            {
                                return deserialized;
                            }
                        }
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
    }

    internal static string ResolveFixturePath(string app, string fixtureFile)
    {
        var root = OsAppHostFixture.RepoRoot();
        var slnx = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var appsProjectRel = slnx.Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .First(p => p is not null && p.EndsWith("/IntoChat.OS.Apps.csproj", StringComparison.Ordinal))!;
        var appsProjectDir = Path.GetDirectoryName(Path.Combine(root, appsProjectRel.Replace('/', Path.DirectorySeparatorChar)))!;
        return Path.Combine(appsProjectDir, "views", app, fixtureFile);
    }

    internal static string CanonicalJson(View view)
        => JsonSerializer.Serialize(view, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
}
