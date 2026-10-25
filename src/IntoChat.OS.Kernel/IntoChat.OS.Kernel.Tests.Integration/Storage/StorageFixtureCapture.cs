using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Web;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Kernel.Testing.Storage;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
using IntoChat.OS.Modules.Time.Timers.Signals;

namespace IntoChat.OS.Kernel.Tests.Integration.Storage;

// The fixture tool (clean-core W1, section 4.4). Run explicitly on a clean checkout of the build the fixture is named
// after (the test executable with --filter-method "*CaptureFromThisBuild" --explicit only): it composes the OS in
// process, drives representative scenarios into brains sealed with the test seal key, and writes Fixtures/{Source}.
public sealed class StorageFixtureCapture(OsHost host)
{
    internal const string Source = "bdb79fc21";

    [Fact(Explicit = true)]
    public async Task CaptureFromThisBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(SourceDirectory(), "Fixtures", Source);
        Assert.False(Directory.Exists(output), $"Remove {output} before capturing it again.");
        var work = Path.Combine(Path.GetTempPath(), "intochat-os-fixture-" + Guid.NewGuid().ToString("N"));
        // work is the capture host's Home; its brain log is <Home>/state.
        var store = Path.Combine(work, IntoChat.OS.Framework.StateHome.StateDirectoryName);
        var capture = new Capture();
        try
        {
            // Legacy and never checkpointed: the shape of every hosted brain today. Its circle brain is Legacy too.
            await RunAsync(work, BrainLogFormat.Legacy, checkpoint: false, capture, ct);
            // Framed and checkpointed: a snapshot, then a framed segment that continues after it.
            await RunAsync(work, BrainLogFormat.Framed, checkpoint: true, capture, ct);

            Directory.CreateDirectory(Path.Combine(output, StorageFixtures.BrainStore));
            foreach (var brainId in capture.Brains)
            {
                var source = StorageFixtures.DirectoryOf(store, brainId);
                var target = Path.Combine(output, StorageFixtures.BrainStore, Path.GetFileName(source));
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(source).Where(file => Path.GetFileName(file) is not "writer.lock" && !file.EndsWith(".tmp", StringComparison.Ordinal)))
                { File.Copy(file, Path.Combine(target, Path.GetFileName(file))); }
            }
            var omitted = Directory.EnumerateDirectories(store).Count() - capture.Brains.Count;
            capture.Scenarios.Add($"omitted {omitted} brain directories whose brain id the scenarios do not name");

            Directory.CreateDirectory(Path.Combine(output, StorageFixtures.GrainState));
            var blobs = new List<StorageFixtureBlob>();
            foreach (var (payload, index) in capture.GrainState.Select((payload, index) => (payload, index)))
            {
                var file = $"{index:D3}.bin";
                await File.WriteAllBytesAsync(Path.Combine(output, StorageFixtures.GrainState, file), payload.Payload, ct);
                blobs.Add(new(file, payload.Store, payload.GrainId, payload.StateName));
            }

            // Read back with this build, as the fixture fact will, and record what it must keep finding.
            var copy = StorageFixtureFacts.CopyBrainStore(output);
            try
            {
                var brains = new List<StorageFixtureBrain>();
                foreach (var brainId in capture.Brains)
                {
                    var read = await StorageFixtures.ReadBrainAsync(host.Brain.Services, copy, brainId, ct);
                    Assert.True(read.Unreadable.Length == 0, string.Join('\n', read.Unreadable));
                    brains.Add(new(brainId, read.Format, read.Snapshot, read.Records.Length, read.Signals.Length, StorageFixtureFacts.SignalTypes(read.Signals)));
                }
                StorageFixtures.WriteManifest(output, new(Source, CapturedFrom(), nameof(TestSealKey), [.. brains], [.. blobs], [.. capture.Scenarios]));
            }
            finally { Directory.Delete(copy, recursive: true); }
        }
        finally
        {
            if (Directory.Exists(work)) { Directory.Delete(work, recursive: true); }
        }
    }

    private async Task RunAsync(string home, BrainLogFormat format, bool checkpoint, Capture capture, CancellationToken ct)
    {
        await using var brain = await OsTestBrain.Create(host.Model, host.Tokens).WithFixtureStorage(home, format).StartAsync(ct);
        using var person = await brain.PersonAsync("fixture-" + format.ToString().ToLowerInvariant(), ct);
        capture.Brains.Add(person.BrainId);
        var step = Steps(capture, format);

        await step("identity: register a person and birth their brain", () => Task.CompletedTask);
        await step("connections: connect Gmail through the OAuth callback", () => ConnectGmailAsync(person, ct));
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "fixture"));
        await step("apps: create an app and report its activity", async () =>
        {
            using (person.AsCaller())
            {
                await app.Create(new AppManifest("Fixture", "A fixture app", [], [], []), "Reports activity.");
                await app.Progress("working", "writing the fixture");
            }
        });
        await step("time: a one-shot timer ticks", () => TickAsync(brain, person, "fixture-timer", ct));
        await step("circles: create a circle brain", async () =>
        {
            using (person.AsCaller()) { capture.Brains.Add(await brain.Get<ICircleFactory>(ICircleFactory.Key(person.BrainId)).Create("fixture-circle", "fixture-circle")); }
        });
        await step("chat: project a conversation", async () =>
        {
            using (person.AsCaller())
            {
                await brain.Get<IChat>(person.Key("fixture-chat")).Project(new ChatProjection("fixture", 1, 1,
                    [new ChatEntry("m1", ChatRole.User, "hello"), new ChatEntry("m2", ChatRole.Assistant, "hi")]));
            }
        });
        // The media neurons carry implicit grain names (no [GrainType]); without a provider each publishes a failure.
        await step("ai media: image generation without a provider publishes from an implicitly named neuron", async () =>
        {
            using (person.AsCaller())
            { await Assert.ThrowsAnyAsync<Exception>(() => brain.Get<IImageGenerator>(person.Key("fixture-images")).Generate(new ImageGenerationRequest("a lighthouse"))); }
        });
        await step("ai media: embeddings publish from an implicitly named neuron", async () =>
        {
            using (person.AsCaller())
            {
                try { await brain.Get<IEmbeddingModel>(person.Key("fixture-embeddings")).Embed(new EmbeddingRequest(["hello"])); }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { /* published as a failure */ }
            }
        });
        if (checkpoint)
        {
            await step("storage: checkpoint the brain into a snapshot", () => StorageFixtures.CheckpointAsync(brain.Services, person.BrainId, ct));
            await step("apps: report activity after the snapshot", async () => { using (person.AsCaller()) { await app.Progress("completed", "after the snapshot"); } });
            await step("time: a timer ticks after the snapshot", () => TickAsync(brain, person, "fixture-timer-2", ct));
        }
        capture.GrainState.AddRange(StorageFixtures.GrainStatePayloads(brain.Services));
    }

    private static Func<string, Func<Task>, Task> Steps(Capture capture, BrainLogFormat format) => async (name, run) =>
    {
        try
        {
            await run();
            capture.Scenarios.Add($"{format}: {name}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { capture.Scenarios.Add($"{format}: FAILED {name}: {error.GetType().Name}: {error.Message}"); }
    };

    private static async Task TickAsync(ITestBrain brain, Person person, string name, CancellationToken ct)
    {
        var timer = brain.Get<ITimer>(person.Key(name));
        SignalProbe<TimerTick> ticks;
        using (person.AsCaller()) { ticks = await brain.Observe<TimerTick>(timer, ct); }
        await using (ticks)
        {
            using (person.AsCaller()) { await timer.Start(TimeSpan.FromMilliseconds(10)); }
            await ticks.NextAsync(ct: ct);
        }
    }

    private async Task ConnectGmailAsync(Person person, CancellationToken ct)
    {
        using var start = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/connections/mail/login", new { connectorId = "gmail", authMethodId = "google-oauth" }, ct);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var redirect = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("url").GetString()!);
        var state = HttpUtility.ParseQueryString(redirect.Query)["state"]!;
        using var callback = await person.Http.GetAsync($"/connections/oauth/gmail/callback?code=fake-code&state={Uri.EscapeDataString(state)}", ct);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.NotNull(host.Tokens);
    }

    // The exact build: the informational version carries the commit the test assembly was built from. Capture from a
    // clean, pushed commit so the label resolves.
    private static string CapturedFrom()
        => typeof(StorageFixtureCapture).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The test assembly carries no informational version.");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private sealed class Capture
    {
        public List<string> Brains { get; } = [];
        public List<string> Scenarios { get; } = [];
        public List<(string Store, string GrainId, string StateName, byte[] Payload)> GrainState { get; } = [];
    }
}
