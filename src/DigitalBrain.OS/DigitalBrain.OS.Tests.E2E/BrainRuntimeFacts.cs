using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.OS.Tests.E2E;

// The real path a click takes, as the signed-in user: the brain compiles the app's behavior, the brain
// runtime loads it into the brain's session in the csharp-sandbox container, and the behavior answers
// through the runtime edge. Needs a Docker daemon with Linux containers.
public sealed class BrainRuntimeFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 240_000)]
    public async Task AClickReachesABehaviorAndBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = Brain.WorkspaceId;
        var bundle = new DirectoryShippedAppSource(Path.Combine(RepositoryRoot(), "src", "IntoChat", "Apps"), "intochat")
            .Load().Single(candidate => candidate.Name == "word-count");
        (await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/import", bundle, ct)).EnsureSuccessStatusCode();

        // A connected session answers in milliseconds; two minutes covers the brain's first session.
        using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answer.CancelAfter(TimeSpan.FromMinutes(2));
        using var asked = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/{bundle.Name}/ask",
            new { operation = "ask", input = "a click reaches a behavior" }, answer.Token);

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal("5 words", await asked.Content.ReadAsStringAsync(ct));
        Assert.Equal(SessionStatus.Connected, (await Brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(scope)).Inspect(ct)).Session);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("The app bundle is read from the repository; run the test from inside it.");
    }
}
