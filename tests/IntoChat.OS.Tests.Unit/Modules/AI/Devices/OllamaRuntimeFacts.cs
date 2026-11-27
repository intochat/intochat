using System.Net;
using System.Text;
using System.Text.Json;
using IntoChat.OS.LocalSetup;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class OllamaRuntimeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Stream = """
        {"status":"pulling manifest"}
        {"status":"pulling sha256:abcdef0123456789","digest":"sha256:abcdef0123456789","total":3000000000,"completed":1000000000}
        {"status":"pulling sha256:abcdef0123456789","digest":"sha256:abcdef0123456789","total":3000000000,"completed":3000000000}
        {"status":"verifying sha256 digest"}
        {"status":"success"}
        """;

    private sealed class Script(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }

        public Script(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this((_, _) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, RequestBody);
        }
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Connection refused");
    }

    private sealed class StaticOptions(DevicesOptions value) : IOptionsMonitor<DevicesOptions>
    {
        public DevicesOptions CurrentValue => value;
        public DevicesOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DevicesOptions, string?> listener) => null;
    }

    private static OllamaRuntime Runtime(HttpMessageHandler handler, string endpoint = "http://localhost:11434", TimeProvider? clock = null)
    {
        var options = new DevicesOptions { Ollama = new OllamaRuntimeOptions { Enabled = true, Endpoint = endpoint } };
        return new OllamaRuntime(new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }, new StaticOptions(options), clock ?? TimeProvider.System);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(Ct))
        {
            list.Add(item);
        }
        return list;
    }

    private static Script TagsAndShow(string tagName, long size, (string Family, string ParamSize, string[] Caps)? show = null)
    {
        var tagsJson = $$"""{"models":[{"name":"{{tagName}}","size":{{size}},"digest":"sha256:abc"}]}""";
        var showJson = show is null
            ? "{}"
            : $$"""{"details":{"family":"{{show.Value.Family}}","parameter_size":"{{show.Value.ParamSize}}"},"capabilities":{{JsonSerializer.Serialize(show.Value.Caps)}}}""";
        return new Script((req, _) =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/tags")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagsJson, Encoding.UTF8) };
            }
            if (path == "/api/show")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(showJson, Encoding.UTF8) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task PullProgressMapsToInstallStates()
    {
        var states = await Collect(Runtime(new Script(Stream + "\n{\"status\":\"after success\"}")).Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.Equal([InstallState.Downloading, InstallState.Downloading, InstallState.Downloading, InstallState.Verifying, InstallState.Ready], states.Select(static s => s.State));
        Assert.Equal([0, 33.33, 100], states.Where(static s => s.State == InstallState.Downloading).Select(static s => Math.Round(s.Percent, 2)));
    }

    [Fact]
    public async Task TheRequestBodyIsSourceGenerated()
    {
        var script = new Script("""{"status":"success"}""");
        _ = await Collect(Runtime(script).Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.Equal("http://localhost:11434/api/pull", script.RequestUri?.ToString());
        using var doc = JsonDocument.Parse(script.RequestBody!);
        Assert.Equal("qwen3.5:4b", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task AnErrorLineFailsWithItsText()
    {
        var body = "{\"status\":\"pulling manifest\"}\n{\"error\":\"pull model manifest: file does not exist\"}\n";
        var items = await Collect(Runtime(new Script(body)).Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.Equal(InstallState.Failed, items[^1].State);
        Assert.Equal("pull model manifest: file does not exist", items[^1].Error);
    }

    [Fact]
    public async Task StreamEndingWithoutSuccessFailsWithResumeHint()
    {
        var items = await Collect(Runtime(new Script("{\"status\":\"pulling manifest\"}\n")).Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.Equal(InstallState.Failed, items[^1].State);
        Assert.Contains("resume", items[^1].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerErrorStatusFails()
    {
        var items = await Collect(Runtime(new Script("boom", HttpStatusCode.InternalServerError)).Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.Equal(InstallState.Failed, items[^1].State);
        Assert.Contains("500", items[^1].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncataloguedTagsGetCardsFromShow()
    {
        var script = TagsAndShow("llama3.2:3b", 2_019_393_189, ("llama", "3.2B", ["completion", "tools"]));
        var card = Assert.Single(await Collect(Runtime(script).Installed(Ct)));
        Assert.Equal(("Llama 3.2B", Abilities.Text | Abilities.Tools, Tier.Fast, Where.Device, 2_019_393_189L), (card.Name, card.Can, card.Tier, card.Where, card.Bytes));
    }

    [Fact]
    public async Task CataloguedTagsKeepTheirCard()
    {
        var script = TagsAndShow("qwen3.5:4b", 3_400_000_000);
        var card = Assert.Single(await Collect(Runtime(script).Installed(Ct)));
        Assert.Equal(KnownModels.Qwen35.Name, card.Name);
        Assert.Equal(3_400_000_000L, card.Bytes);
    }

    [Fact]
    public async Task UnreachableIsNotAnException()
    {
        var status = await Runtime(new Refuse()).Probe(Ct);
        Assert.False(status.Reachable);
        Assert.Equal(RuntimeId.Ollama, status.Id);
    }

    [Fact]
    public async Task LoadedModelsComeFromApiPs()
    {
        var script = new Script("""{"models":[{"name":"qwen3.5:4b","size":3400000000,"size_vram":3400000000}]}""");
        var loaded = await Runtime(script).Loaded(Ct);
        Assert.Equal([new ModelId("qwen3.5:4b")], loaded);
    }

    [Fact]
    public async Task RemoveDeletesViaApiDelete()
    {
        var script = new Script("{}", HttpStatusCode.OK);
        await Runtime(script).Remove(new ModelId("qwen3.5:4b"), Ct);
        Assert.Equal(HttpMethod.Delete, script.RequestMethod);
        using var doc = JsonDocument.Parse(script.RequestBody!);
        Assert.Equal("qwen3.5:4b", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void LocalSetupHasNoPullerLeft() =>
        Assert.Null(typeof(Preflight).Assembly.GetType("IntoChat.OS.LocalSetup.ModelPuller"));

    [Fact]
    public async Task ConsoleProgressPrintsOnlyChangedLines()
    {
        using var writer = new StringWriter();
        var sink = new ConsolePullProgress(writer);
        await foreach (var update in OllamaRuntime.Pull(
            new Script("{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling manifest\"}\n{\"status\":\"success\"}"),
            new Uri("http://localhost:11434"),
            new ModelId("qwen3.5:4b"),
            Ct))
        {
            sink.Report(update);
        }
        Assert.Equal(2, writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task PreflightPullFlagPullsAMissingModelAndFailsOnError()
    {
        var okEnv = FakeProbeEnvironment.FromJson(NoModelWithPull("{\"status\":\"success\"}"));
        using var writer = new StringWriter();
        Assert.Equal(0, await Preflight.PullIfRequestedAsync(okEnv, LauncherCommand.Parse(["preflight", "--pull"]), await Collected(okEnv), writer, Ct));
        Assert.Contains("success", writer.ToString(), StringComparison.Ordinal);

        var badEnv = FakeProbeEnvironment.FromJson(NoModelWithPull("{\"error\":\"nope\"}"));
        Assert.Equal(1, await Preflight.PullIfRequestedAsync(badEnv, LauncherCommand.Parse(["preflight", "--pull"]), await Collected(badEnv), writer, Ct));
        Assert.Contains("nope", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAndInstalledCacheWithinTtlAndRefreshAfterExpiry()
    {
        var clock = new FakeTimeProvider();
        var versionCalls = 0;
        var tagsCalls = 0;
        var script = new Script((req, _) =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/version")
            {
                versionCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"version":"0.9.0"}""", Encoding.UTF8) };
            }
            if (path == "/api/tags")
            {
                tagsCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"models":[{"name":"qwen3.5:4b","size":3400000000,"digest":"sha256:abc"}]}""", Encoding.UTF8),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var runtime = Runtime(script, clock: clock);

        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Equal((1, 1), (versionCalls, tagsCalls));

        clock.Advance(OllamaRuntime.CacheTtl + TimeSpan.FromSeconds(1));

        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Equal((2, 2), (versionCalls, tagsCalls));
    }

    [Fact]
    public async Task InstallAndRemoveInvalidateCachedProbeAndInstalled()
    {
        var clock = new FakeTimeProvider();
        var versionCalls = 0;
        var tagsCalls = 0;
        var script = new Script((req, _) =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/api/version" => Count(ref versionCalls, """{"version":"0.9.0"}"""),
                "/api/tags" => Count(ref tagsCalls, """{"models":[{"name":"qwen3.5:4b","size":3400000000,"digest":"sha256:abc"}]}"""),
                "/api/pull" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"success"}""", Encoding.UTF8) },
                "/api/delete" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        static HttpResponseMessage Count(ref int counter, string body)
        {
            counter++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        }

        var runtime = Runtime(script, clock: clock);

        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Equal((1, 1), (versionCalls, tagsCalls));

        _ = await Collect(runtime.Install(new ModelId("qwen3.5:4b"), Ct));
        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Equal((2, 2), (versionCalls, tagsCalls));

        await runtime.Remove(new ModelId("qwen3.5:4b"), Ct);
        Assert.True((await runtime.Probe(Ct)).Reachable);
        Assert.Single(await Collect(runtime.Installed(Ct)));
        Assert.Equal((3, 3), (versionCalls, tagsCalls));
    }

    [Fact]
    public async Task UnreachableProbeShortCircuitsInstalledAndLoadedWithinTtl()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        var script = new Script((_, _) =>
        {
            calls++;
            throw new HttpRequestException("Connection refused");
        });

        var runtime = Runtime(script, clock: clock);

        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Empty(await Collect(runtime.Installed(Ct)));
        Assert.Empty(await runtime.Loaded(Ct));
        Assert.Equal(1, calls);

        clock.Advance(OllamaRuntime.CacheTtl + TimeSpan.FromSeconds(1));
        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Equal(2, calls);
    }

    private static Task<PreflightReport> Collected(IProbeEnvironment environment)
        => Preflight.CollectAsync(environment, new PreflightOptions(new LocalModelOptions(), true, "repo"), Ct);

    private static string NoModelWithPull(string pullBody)
        => "{\"os\":\"linux\",\"freeBytes\":{\"*\":200000000000},\"http\":{"
            + "\"GET http://localhost:11434/api/version\":{\"status\":200,\"body\":\"{}\"},"
            + "\"GET http://localhost:11434/api/tags\":{\"status\":200,\"body\":\"{\\\"models\\\":[]}\"},"
            + "\"POST http://localhost:11434/api/pull\":{\"status\":200,\"body\":" + JsonSerializer.Serialize(pullBody) + "}}}";
}
