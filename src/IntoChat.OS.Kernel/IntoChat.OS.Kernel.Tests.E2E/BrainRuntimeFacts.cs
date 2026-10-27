using System.Net.Http.Json;
using System.Xml.Linq;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text.Signals;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The real path a request takes, as the signed-in user: the shell posts a request to the app, the brain
// publishes it, the app's synapse reacts in the brain's session in the csharp-sandbox container, and the
// answer is a neuron the caller reads. Needs a Docker daemon with Linux containers.
[Collection("Reference host")]
public sealed class BrainRuntimeFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    [Fact(Timeout = 240_000)]
    public async Task ClearingAConversationUpdatesItsRunningShippedProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var scope = WorkspaceId;
        var appKey = scope + "/apps/ino";
        Assert.Equal(AppStatus.Live, (await Brain.Get<IApp>(appKey).Read()).Status);
        await AppRuntimeReadiness.WaitAsync(Brain, User, ct);
        var chat = Brain.Get<IChat>(appKey + "/transcript");
        var selection = (await chat.Read()).Selection;
        Assert.NotNull(selection);
        var agent = Brain.Get<IAgent>(selection.Agent);
        await using var changes = await Brain.Observe<ChatChanged>(chat, ct);
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{scope}/apps/ino/request",
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
}
