using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Testing;
using DigitalBrain.Microsoft.Aspire;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

// The live silos run the published build {Workdir}/live.json names; without one the AppHost runs from source.
public sealed class LiveBuildHostingFacts : IDisposable
{
    private readonly string workdir = Directory.CreateTempSubdirectory("live-build-").FullName;

    public void Dispose() => Directory.Delete(workdir, recursive: true);

    [Fact]
    public async Task TheSiloRunsTheLiveBuildWhenOneIsSet()
    {
        SetLive("abc1234");

        var silo = AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll");

        Assert.NotNull(silo);
        Assert.Equal("dotnet", silo.Resource.Command);
        Assert.Equal([Path.Combine(workdir, "builds", "abc1234", "os", "DigitalBrain.OS.dll")], await ArgumentsOf(silo.Resource));
    }

    [Fact]
    public async Task ARestartReadsTheBuildSetSince()
    {
        SetLive("abc1234");
        var silo = AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll")!;

        SetLive("def5678");

        Assert.Equal([Path.Combine(workdir, "builds", "def5678", "os", "DigitalBrain.OS.dll")], await ArgumentsOf(silo.Resource));
    }

    [Fact]
    public void WithoutALiveBuildTheAppHostRunsFromSource()
        => Assert.Null(AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll"));

    private void SetLive(string sha)
    {
        var build = Path.Combine(workdir, "builds", sha);
        File.WriteAllText(Path.Combine(workdir, "live.json"),
            System.Text.Json.JsonSerializer.Serialize(new { sha, path = build }));
    }

    private static async Task<string[]> ArgumentsOf(IResource resource)
    {
        var args = new List<object>();
        foreach (var callback in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await callback.Callback(new CommandLineArgsCallbackContext(args, resource, TestContext.Current.CancellationToken));
        }
        return [.. args.Select(arg => arg.ToString()!)];
    }
}
