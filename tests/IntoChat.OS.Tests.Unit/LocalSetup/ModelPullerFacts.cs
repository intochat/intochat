using System.Net;
using System.Text;

namespace IntoChat.OS.LocalSetup.Tests.Unit;

public sealed class ModelPullerFacts
{
    private sealed class Script(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        }
    }

    private sealed class Collect : IProgress<PullProgress>
    {
        public List<PullProgress> Items { get; } = [];

        public void Report(PullProgress value) => Items.Add(value);
    }

    private const string Stream = """
        {"status":"pulling manifest"}
        {"status":"pulling sha256:abcdef0123456789","digest":"sha256:abcdef0123456789","total":3000000000,"completed":1000000000}
        {"status":"pulling sha256:abcdef0123456789","digest":"sha256:abcdef0123456789","total":3000000000,"completed":3000000000}
        {"status":"verifying sha256 digest"}
        {"status":"success"}
        """;

    [Fact]
    public async Task ProgressIsReportedInOrderAndSuccessTerminates()
    {
        var progress = new Collect();
        var result = await ModelPuller.PullAsync(new Script(Stream + "\n{\"status\":\"after success\"}"), new LocalModelOptions(), progress, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(["pulling manifest", "pulling sha256:abcdef0123456789", "pulling sha256:abcdef0123456789", "verifying sha256 digest", "success"], progress.Items.Select(item => item.Status));
        Assert.Equal(1000000000, progress.Items[1].Completed);
        Assert.Equal(3000000000, progress.Items[1].Total);
    }

    [Fact]
    public void LayerLinesRenderDigestSizeAndPercent()
    {
        var line = new PullProgress("pulling x", "sha256:abcdef0123456789", 3_650_722_201, 1_288_490_189).Render();
        Assert.Equal("pulling abcdef01 1.2/3.4 GB 35%", line);
        Assert.Equal("verifying sha256 digest", new PullProgress("verifying sha256 digest", null, null, null).Render());
    }

    [Fact]
    public async Task ErrorLineFailsWithItsMessage()
    {
        var body = "{\"status\":\"pulling manifest\"}\n{\"error\":\"pull model manifest: file does not exist\"}\n";
        var result = await ModelPuller.PullAsync(new Script(body), new LocalModelOptions(), new Collect(), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal("pull model manifest: file does not exist", result.Error);
    }

    [Fact]
    public async Task StreamEndingWithoutSuccessFails()
    {
        var result = await ModelPuller.PullAsync(new Script("{\"status\":\"pulling manifest\"}\n"), new LocalModelOptions(), new Collect(), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Contains("resume", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultAndOverrideTagsAreSentInTheRequestBody()
    {
        var defaults = new Script("{\"status\":\"success\"}");
        await ModelPuller.PullAsync(defaults, new LocalModelOptions(), new Collect(), TestContext.Current.CancellationToken);
        Assert.Equal("http://localhost:11434/api/pull", defaults.RequestUri?.ToString());
        Assert.Contains("\"model\":\"qwen3.5:4b\"", defaults.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", defaults.RequestBody, StringComparison.Ordinal);

        var small = new Script("{\"status\":\"success\"}");
        await ModelPuller.PullAsync(small, new LocalModelOptions { Model = LocalModelOptions.SmallModel }, new Collect(), TestContext.Current.CancellationToken);
        Assert.Contains("\"model\":\"qwen3.5:2b\"", small.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonLoopbackEndpointIsRejectedUnlessExplicit()
    {
        var handler = new Script("{\"status\":\"success\"}");
        var rejected = await ModelPuller.PullAsync(handler, new LocalModelOptions { Endpoint = "http://10.0.0.5:11434" }, new Collect(), TestContext.Current.CancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Null(handler.RequestBody);

        var allowed = await ModelPuller.PullAsync(handler, new LocalModelOptions { Endpoint = "http://10.0.0.5:11434", EndpointExplicit = true }, new Collect(), TestContext.Current.CancellationToken);
        Assert.True(allowed.Succeeded);
    }

    [Theory]
    [InlineData("hf.co/user/model", false)]
    [InlineData("registry.example.com:5000/team/model:1", false)]
    [InlineData("library/qwen3.5:4b", true)]
    [InlineData("qwen3.5:2b", true)]
    public async Task RegistryHostNeedsAllowRegistry(string model, bool allowedByDefault)
    {
        var handler = new Script("{\"status\":\"success\"}");
        var blocked = await ModelPuller.PullAsync(handler, new LocalModelOptions { Model = model }, new Collect(), TestContext.Current.CancellationToken);
        Assert.Equal(allowedByDefault, blocked.Succeeded);
        var opted = await ModelPuller.PullAsync(handler, new LocalModelOptions { Model = model, AllowRegistry = true }, new Collect(), TestContext.Current.CancellationToken);
        Assert.True(opted.Succeeded);
    }

    [Fact]
    public async Task ServerErrorStatusFails()
    {
        var result = await ModelPuller.PullAsync(new Script("boom", HttpStatusCode.InternalServerError), new LocalModelOptions(), new Collect(), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleProgressPrintsOnlyChangedLines()
    {
        using var writer = new StringWriter();
        var sink = new ConsolePullProgress(writer);
        await ModelPuller.PullAsync(new Script("{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling manifest\"}\n{\"status\":\"success\"}"), new LocalModelOptions(), sink, TestContext.Current.CancellationToken);
        Assert.Equal(2, writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task PreflightPullFlagPullsAMissingModelAndFailsOnError()
    {
        var okEnv = FakeProbeEnvironment.FromJson(NoModelWithPull("{\"status\":\"success\"}"));
        using var writer = new StringWriter();
        Assert.Equal(0, await Preflight.PullIfRequestedAsync(okEnv, LauncherCommand.Parse(["preflight", "--pull"]), await Collected(okEnv), writer, TestContext.Current.CancellationToken));
        Assert.Contains("success", writer.ToString(), StringComparison.Ordinal);

        var badEnv = FakeProbeEnvironment.FromJson(NoModelWithPull("{\"error\":\"nope\"}"));
        Assert.Equal(1, await Preflight.PullIfRequestedAsync(badEnv, LauncherCommand.Parse(["preflight", "--pull"]), await Collected(badEnv), writer, TestContext.Current.CancellationToken));
        Assert.Contains("nope", writer.ToString(), StringComparison.Ordinal);
    }

    private static Task<PreflightReport> Collected(IProbeEnvironment environment)
        => Preflight.CollectAsync(environment, new PreflightOptions(new LocalModelOptions(), true, "repo"), TestContext.Current.CancellationToken);

    private static string NoModelWithPull(string pullBody)
        => "{\"os\":\"linux\",\"freeBytes\":{\"*\":200000000000},\"http\":{"
            + "\"GET http://localhost:11434/api/version\":{\"status\":200,\"body\":\"{}\"},"
            + "\"GET http://localhost:11434/api/tags\":{\"status\":200,\"body\":\"{\\\"models\\\":[]}\"},"
            + "\"POST http://localhost:11434/api/pull\":{\"status\":200,\"body\":" + System.Text.Json.JsonSerializer.Serialize(pullBody) + "}}}";
}
