namespace IntoChat.OS.LocalSetup.Tests.Unit;

public sealed class ToolChecksFacts
{
    [Fact]
    public async Task MissingOllamaFailsWithTheInstallUrl()
    {
        var (exit, output) = await Fixtures.RunAsync("no-ollama");
        Assert.Equal(1, exit);
        Assert.Contains("[FAIL] ollama", output, StringComparison.Ordinal);
        Assert.Contains("https://ollama.com/download", output, StringComparison.Ordinal);
        var report = await Fixtures.CollectAsync("no-ollama");
        Assert.DoesNotContain(report.Results, result => result.Id == "model");
        Assert.False(report.OllamaReachable);
    }

    [Fact]
    public async Task MissingModelWarnsAndExitsZero()
    {
        var (exit, output) = await Fixtures.RunAsync("no-model");
        Assert.Equal(0, exit);
        Assert.Contains("[WARN] model", output, StringComparison.Ordinal);
        Assert.True((await Fixtures.CollectAsync("no-model")).ModelMissing);
    }

    [Fact]
    public async Task MissingGpuWarnsAndIsNotDetected()
    {
        var (exit, output) = await Fixtures.RunAsync("no-gpu");
        Assert.Equal(0, exit);
        Assert.Contains("CPU mode, expect slow answers", output, StringComparison.Ordinal);
        Assert.False((await Fixtures.CollectAsync("no-gpu")).GpuDetected);
    }

    [Fact]
    public async Task AllGreenDetectsGpuAndEveryToolPasses()
    {
        var report = await Fixtures.CollectAsync("all-green");
        Assert.True(report.GpuDetected);
        Assert.True(report.OllamaReachable);
        Assert.False(report.ModelMissing);
        Assert.True(report.FlutterAvailable);
        Assert.All(["ollama", "model", "gpu", "dotnet-sdk", "flutter"], id => Assert.Equal(CheckStatus.Pass, Fixtures.Result(report, id).Status));
        Assert.Equal(0, report.ExitCode());
    }

    [Fact]
    public async Task ModelOverrideChangesWhichTagIsRequired()
    {
        var report = await Fixtures.CollectAsync("all-green", new LocalModelOptions { Model = LocalModelOptions.SmallModel });
        Assert.Equal(CheckStatus.Warn, Fixtures.Result(report, "model").Status);
    }

    [Theory]
    [InlineData("flutter", "flutter --version")]
    [InlineData("dotnet-sdk", "dotnet --version")]
    public async Task MissingToolProducesFixLine(string id, string command)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Fixtures.PathOf("all-green"), TestContext.Current.CancellationToken))!;
        json["commands"]!.AsObject().Remove(command);
        var report = await Preflight.CollectAsync(FakeProbeEnvironment.FromJson(json.ToJsonString()), new PreflightOptions(new LocalModelOptions(), true, "repo"), TestContext.Current.CancellationToken);
        var result = Fixtures.Result(report, id);
        Assert.NotEqual(CheckStatus.Pass, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Fix));
        Assert.False(report.FlutterAvailable && id == "flutter");
    }

    [Theory]
    [InlineData("""{"models":[{"name":"qwen3.5:4b"}]}""", "qwen3.5:4b", true)]
    [InlineData("""{"models":[{"name":"qwen3.5:4b"}]}""", "qwen3.5:2b", false)]
    [InlineData("""{"models":[{"name":"tiny:latest"}]}""", "tiny", true)]
    [InlineData("not json", "qwen3.5:4b", false)]
    public void TagsParsing(string json, string model, bool present)
        => Assert.Equal(present, Checks.ModelPresentCheck.Contains(json, model));
}
