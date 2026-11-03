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
            new("post", "open", "{}", "open.json"),
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
        "post",
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
        var expectedJson = CanonicalJson(expectedFixture!.Main!);
        var actualJson = CanonicalJson(actualView);
        Assert.Equal(expectedJson, actualJson);
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
