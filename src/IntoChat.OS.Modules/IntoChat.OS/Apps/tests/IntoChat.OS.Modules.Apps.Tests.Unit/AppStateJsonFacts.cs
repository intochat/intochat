using System.Text.Json;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// 06-08: the plain app neuron stores AppState as JSON with the runtime's wire options. Everything the app reads back must survive
// the round trip: the maps behind their views, history records, retained requests and the last activity.
public sealed class AppStateJsonFacts
{
    [Fact]
    public void AnAppStateRoundTripsThroughTheRuntimeJson()
    {
        var manifest = new AppManifest("Echo", "echoes", [new("open", "opens")], [new("tone", "Tone", "calm")], []);
        var state = new AppState { Manifest = manifest, Spec = "spec", Version = "1.2.3", Status = AppStatus.Live, Origin = new("os", new AppSource.Shipped(), "echo", "1.2.3") };
        state.PutFile("synapses/echo.cs", "// echo");
        state.PutSetting("tone", "loud");
        state.PutConnection("db", "postgres/main");
        state.History = [new AppVersionRecord("1.2.3", DateTimeOffset.UnixEpoch, manifest, "spec", state.Files)];
        var request = new AppRequested("open", "now", Guid.NewGuid());
        state.RequestRetention = [new RetainedRequest(1, request)];
        state.RequestSequence = 1;
        state.LastActivity = new AppActivity("echo", "working", "busy");

        var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;

        // The maps' views are written beside their entry arrays (the state export reads them) and read back to the same maps.
        Assert.Equal(state.Files, read.Files);
        Assert.Equal(state.Settings, read.Settings);
        Assert.Equal(state.Connections, read.Connections);
        Assert.Equal(manifest.Title, read.Manifest!.Title);
        Assert.Equal(("1.2.3", AppStatus.Live), (read.Version, read.Status));
        Assert.IsType<AppSource.Shipped>(read.Origin!.Source);
        Assert.Equal(state.Files, Assert.Single(read.History).Files);
        Assert.Equal(request, Assert.Single(read.RequestRetention).Request);
        Assert.Equal(state.LastActivity, read.LastActivity);
    }

    [Fact]
    public void RuntimeStateSurvivesJsonRoundTrip()
    {
        var state = new AppState
        {
            RuntimeStateJson = "{\"count\":42,\"text\":\"hello\"}",
            RuntimeStateRevision = 7
        };

        var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;

        Assert.Equal("{\"count\":42,\"text\":\"hello\"}", read.RuntimeStateJson);
        Assert.Equal(7, read.RuntimeStateRevision);

        var cloned = state.Clone();
        Assert.Equal("{\"count\":42,\"text\":\"hello\"}", cloned.RuntimeStateJson);
        Assert.Equal(7, cloned.RuntimeStateRevision);
    }

    [Fact]
    public void SignalCursorAndDeadLettersDefaultToZeroAndEmpty()
    {
        var state = new AppState();
        Assert.Equal(0L, state.SignalCursor);
        Assert.NotNull(state.DeadLetters);
        Assert.Empty(state.DeadLetters);

        var json = "{}";
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;
        Assert.Equal(0L, read.SignalCursor);
        Assert.NotNull(read.DeadLetters);
        Assert.Empty(read.DeadLetters);
    }

    [Fact]
    public void SignalCursorAndDeadLettersSurviveJsonRoundTripAndClone()
    {
        var at = DateTimeOffset.UtcNow;
        var deadLetter = new DigitalBrain.DeadLetterEntry(42L, "b1/chat/p", "chat.message", "Failed reaction", at);
        var state = new AppState
        {
            SignalCursor = 987654321L,
            DeadLetters = [deadLetter]
        };

        var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;

        Assert.Equal(987654321L, read.SignalCursor);
        var readEntry = Assert.Single(read.DeadLetters);
        Assert.Equal(42L, readEntry.Position);
        Assert.Equal("b1/chat/p", readEntry.Publisher);
        Assert.Equal("chat.message", readEntry.SignalType);
        Assert.Equal("Failed reaction", readEntry.ErrorMessage);
        Assert.Equal(at, readEntry.At);

        var cloned = state.Clone();
        Assert.Equal(987654321L, cloned.SignalCursor);
        var clonedEntry = Assert.Single(cloned.DeadLetters);
        Assert.Equal(42L, clonedEntry.Position);
        Assert.Equal("b1/chat/p", clonedEntry.Publisher);
        Assert.Equal("chat.message", clonedEntry.SignalType);
        Assert.Equal("Failed reaction", clonedEntry.ErrorMessage);
        Assert.Equal(at, clonedEntry.At);
    }

    [Fact]
    public void Large64BitCursorSurvivesJsonRoundTripAndClone()
    {
        long[] largeCursors = [10_000_000_000L, long.MaxValue - 1, long.MaxValue];
        foreach (var cursor in largeCursors)
        {
            var state = new AppState { SignalCursor = cursor };
            var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
            var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;
            Assert.Equal(cursor, read.SignalCursor);

            var cloned = state.Clone();
            Assert.Equal(cursor, cloned.SignalCursor);
        }
    }

    [Fact]
    public void CloneDeepCopiesDeadLetters_MutatingCloneDoesNotAffectOriginal()
    {
        var at = DateTimeOffset.UtcNow;
        var entry1 = new DigitalBrain.DeadLetterEntry(100L, "b1/chat/p1", "chat.msg", "err1", at);
        var entry2 = new DigitalBrain.DeadLetterEntry(200L, "b1/chat/p2", "chat.msg", "err2", at);
        var state = new AppState
        {
            SignalCursor = 500L,
            DeadLetters = [entry1, entry2]
        };

        var cloned = state.Clone();

        // 1. Array instance must be independent
        Assert.NotSame(state.DeadLetters, cloned.DeadLetters);
        Assert.Equal(2, cloned.DeadLetters.Length);

        // 2. Modifying cloned array element in-place must not mutate the original
        var replacement = new DigitalBrain.DeadLetterEntry(999L, "b1/chat/p3", "chat.replaced", "err_replaced", at);
        cloned.DeadLetters[0] = replacement;

        Assert.Equal(999L, cloned.DeadLetters[0].Position);
        Assert.Equal(100L, state.DeadLetters[0].Position);
        Assert.Equal("b1/chat/p1", state.DeadLetters[0].Publisher);

        // 3. Reassigning cloned array must not mutate the original
        cloned.DeadLetters = [];
        Assert.Empty(cloned.DeadLetters);
        Assert.Equal(2, state.DeadLetters.Length);
    }

    [Fact]
    public void LegacyAppStateJsonWithoutCursorOrDeadLetters_DeserializesWithDefaults()
    {
        // Legacy AppState JSON omitting SignalCursor and DeadLetters
        var legacyJson = """
        {
            "Spec": "sample-spec",
            "Version": "1.0.0",
            "Status": 1,
            "FileEntries": [{"Key": "main.cs", "Value": "// test"}],
            "SettingEntries": [{"Key": "key1", "Value": "val1"}],
            "RuntimeStateRevision": 5
        }
        """;

        var read = JsonSerializer.Deserialize<AppState>(legacyJson, DigitalBrain.Invocation.RuntimeProtocol.Json)!;

        Assert.Equal("sample-spec", read.Spec);
        Assert.Equal("1.0.0", read.Version);
        Assert.Equal(0L, read.SignalCursor);
        Assert.NotNull(read.DeadLetters);
        Assert.Empty(read.DeadLetters);
        Assert.Equal(5L, read.RuntimeStateRevision);
    }
}

