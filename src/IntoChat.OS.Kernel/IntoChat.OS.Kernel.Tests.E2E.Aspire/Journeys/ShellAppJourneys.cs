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
            new("workspace", "open", "{}", "open.json"),
            new("gallery", "open", "{}", "open.json"),
            new("creator", "open", "{}", "open.json"),
            new("ino", "open", "{}", "open.json"),
            new("desktop", "open", "{}", "open.json"),
            new("friends", "open", "{}", "open.json"),
            new("photocraft", "open", "{}", "open.json"),
            new("filmcraft", "open", "{}", "open.json"),
        }.ToDictionary(j => j.App, StringComparer.Ordinal);

    public static TheoryData<string> Apps =>
    [
        "settings",
        "workspace",
        "gallery",
        "creator",
        "ino",
        "desktop",
        "friends",
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

        var baselineRevision = await ReadViewRevisionAsync(journey.App, ct);

        using var postResponse = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/{journey.App}/request",
            new { operation = journey.Operation, input = journey.Argument },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var actualView = await PollPresentedViewAsync(journey.App, baselineRevision, ct);
        var diff = ViewComparison.Diff(expectedFixture!.Main!, actualView);
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

        var baselineRevision = await ReadViewRevisionAsync(journey.App, ct);
        using var postResponse = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/{journey.App}/request",
            new { operation = journey.Operation, input = journey.Argument },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var actualView = await PollPresentedViewAsync(journey.App, baselineRevision, ct);
        var driftedExpected = MutateFirstTextNode(expectedFixture!.Main!, "DRIFTED_TEXT_NODE");

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

    internal async Task<long> ReadViewRevisionAsync(string app, CancellationToken ct)
    {
        using var response = await host.Os.GetAsync($"/brains/{host.Scope}/apps/{app}", ct);
        if (response.StatusCode != HttpStatusCode.OK) { return 0L; }
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("viewRevision", out var revElem) && revElem.TryGetInt64(out var rev)
            ? rev
            : 0L;
    }

    internal async Task<View> PollPresentedViewAsync(string app, long minExclusiveRevision, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await host.Os.GetAsync($"/brains/{host.Scope}/apps/{app}", deadline.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(deadline.Token),
                    cancellationToken: deadline.Token);
                var currentRevision = doc.RootElement.TryGetProperty("viewRevision", out var revElem) && revElem.TryGetInt64(out var rev)
                    ? rev
                    : 0L;
                if (currentRevision > minExclusiveRevision
                    && doc.RootElement.TryGetProperty("view", out var viewElem)
                    && viewElem.ValueKind == JsonValueKind.Object)
                {
                    var deserialized = JsonSerializer.Deserialize<View>(viewElem.GetRawText(), JsonOptions);
                    if (deserialized is not null)
                    {
                        return deserialized;
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
