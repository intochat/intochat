using System.Net.Http.Json;
using System.Xml.Linq;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.OS.Tests.E2E;

// The real path a request takes, as the signed-in user: the shell posts a request to the app, the brain
// publishes it, the app's synapse reacts in the brain's session in the csharp-sandbox container, and the
// answer is a neuron the caller reads. Needs a Docker daemon with Linux containers.
public sealed class BrainRuntimeFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 240_000)]
    public async Task ClearingAConversationUpdatesItsRunningShippedProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = WorkspaceId;
        var appKey = scope + "/apps/assistant";
        Assert.Equal(AppStatus.Live, (await Brain.Get<IApp>(appKey).Read()).Status);
        var agent = Brain.Get<IAgent>(appKey + "/conversations/main");
        var chat = Brain.Get<IChat>(appKey + "/transcript");
        await using var changes = await Brain.Observe<ChatChanged>(chat, ct);
        using var sent = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/assistant/request",
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
    }

    [Fact(Timeout = 240_000)]
    public async Task ARequestReachesASynapseAndItsAnswerIsANeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = WorkspaceId;
        var bundle = new FeatureDirectory(Features()).Load().Single(candidate => candidate.Owner == "intochat" && candidate.Bundle.Name == "word-count").Bundle;
        (await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/import", bundle, ct)).EnsureSuccessStatusCode();

        var runtime = Brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(scope));

        var answer = Brain.Get<IText>($"{scope}/apps/{bundle.Name}/answer");
        await using var changes = await Brain.Observe<TextChanged>(answer, ct);

        using var requested = await Brain.HttpClient.PostAsJsonAsync($"/brains/{scope}/apps/{bundle.Name}/request",
            new { operation = "ask", input = "a click reaches a behavior" }, ct);

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        while ((await answer.Read()).Markdown != "5 words") { await changes.NextAsync(ct: ct); }
        Assert.Equal(SessionStatus.Connected, (await runtime.Inspect(ct)).Session);
    }

    // The AppHost hands the OS the repository's features folder, beside the solution (AppHost.cs).
    private static string Features()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var solution = Path.Combine(directory.FullName, "DigitalBrain.slnx");
            if (File.Exists(solution)) { return Path.Combine(directory.FullName, "features"); }
        }
        throw new InvalidOperationException("The app bundle is read from the repository; run the test from inside it.");
    }
}
