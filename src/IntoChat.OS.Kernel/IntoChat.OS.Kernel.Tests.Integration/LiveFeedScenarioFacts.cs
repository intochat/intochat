using System.Text.Json;
using IntoChat.OS.Apps;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The shell's live feed across a reconnect. The endpoint has no since or Last-Event-ID: on every connect it replays
// each source's last retained signal (replay: true, under its retention sequence) before ready, so a client resumes
// by comparing that sequence with the last one it saw live.
public sealed class LiveFeedScenarioFacts(OsHost host)
{
    [Fact]
    public async Task ALiveSubscriptionReplaysFromSequenceAfterReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ada = await host.PersonAsync("ada", ct);
        // The emission seam: an app in ada's brain reports its activity as the app runtime would.
        var app = host.Brain.Get<IApp>(IApp.Key(ada.BrainId, "echo"));
        using (ada.AsCaller()) { await app.Create(new AppManifest("Echo", "echoes", [], [], []), "Echoes."); }

        long sequence;
        await using (var first = await LiveStream.OpenAsync(ada.Http, ada.BrainId, "AppActivity", ct))
        {
            await first.ReadyAsync(ct);
            using (ada.AsCaller()) { await app.Progress("working", "echoing"); }
            using var live = await first.NextAsync(ct);
            Assert.Equal("AppActivity", live.RootElement.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, live.RootElement.GetProperty("replay").ValueKind);
            sequence = live.RootElement.GetProperty("sequence").GetInt64();
        }

        await using var second = await LiveStream.OpenAsync(ada.Http, ada.BrainId, "AppActivity", ct);
        using var replayed = await second.NextAsync(ct);
        Assert.True(replayed.RootElement.GetProperty("replay").GetBoolean());
        Assert.Equal(sequence, replayed.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("working", replayed.RootElement.GetProperty("payload").GetProperty("state").GetString());
        await second.ReadyAsync(ct);
    }
}
