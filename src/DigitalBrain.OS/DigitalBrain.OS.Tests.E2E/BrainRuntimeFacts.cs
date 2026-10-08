using System.Xml.Linq;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.OS.Tests.E2E;

// The real path a click takes, as the signed-in user: the brain compiles the app's behavior, the brain
// runtime loads it into the brain's session in the csharp-sandbox container, and the behavior answers
// through the runtime edge. Needs a Docker daemon with Linux containers.
public sealed class BrainRuntimeFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 240_000)]
    public async Task ClearingAConversationUpdatesItsRunningShippedProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = Brain.WorkspaceId;
        var appKey = scope + "/apps/assistant";
        Assert.Equal(AppStatus.Live, (await Brain.Get<IApp>(appKey).Read()).Status);
        var agent = Brain.Get<IAgent>(appKey + "/conversations/main");
        var chat = Brain.Get<IChat>(appKey + "/transcript");
        await using var changes = await Brain.Observe<ChatChanged>(chat, ct);
        using var sent = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/assistant/ask",
            new { operation = "send", input = "Remember this conversation" }, ct);
        sent.EnsureSuccessStatusCode();
        ChatProjection? before;
        while ((before = (await chat.Read()).Projection) is null
            || before.Messages.LastOrDefault()?.Status != "completed")
        { await changes.NextAsync(ct: ct); }
        Assert.Equal(2, before.Messages.Length);
        Assert.Equal("You said: Remember this conversation", before.Messages[1].Text);

        await agent.Clear();
        var cleared = await agent.Read();
        ChatProjection? after;
        while ((after = (await chat.Read()).Projection) is null || after.Version < cleared.Version)
        { await changes.NextAsync(ct: ct); }
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Empty(after.Messages);
        Assert.Empty(after.Actions);
        Model.AssertNoProtocolErrors();
    }

    [Fact(Timeout = 240_000)]
    public async Task AClickReachesABehaviorAndBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = Brain.WorkspaceId;
        var bundle = new DirectoryShippedAppSource(IntoChatApps(), "intochat")
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

    // The AppHost hands the OS IntoChat's bundles from the directory beside its own (AppHost.cs);
    // the AppHost project is found through the solution, never by a path literal.
    private static string IntoChatApps()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var solution = Path.Combine(directory.FullName, "DigitalBrain.slnx");
            if (!File.Exists(solution)) { continue; }
            var appHost = XDocument.Load(solution).Descendants("Project")
                .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
                .Single(path => Path.GetFileNameWithoutExtension(path) == "DigitalBrain.AppHost");
            return Path.GetFullPath(Path.Combine(directory.FullName, Path.GetDirectoryName(appHost)!, "..", "IntoChat", "Apps"));
        }
        throw new InvalidOperationException("The app bundle is read from the repository; run the test from inside it.");
    }
}
