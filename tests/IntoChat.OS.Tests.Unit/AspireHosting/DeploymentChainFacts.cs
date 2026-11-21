using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentChainFacts
{
    private static void Chain(AIDeployment ai) => ai
        .Device(d => d.Ollama().FoundryLocal().Attach("lmstudio", new Uri("http://localhost:1234/v1"))
            .Roles(r => r.Chat(KnownModels.Qwen35).Think(KnownModels.Qwen25Coder14B).Hear(KnownModels.WhisperLargeV3Turbo))
            .Prefetch(KnownModels.Qwen35))
        .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash).Think(KnownModels.Gpt56Luna).Embed(KnownModels.TextEmbedding3Small)))
        .Web(w => w.SearXng().MicrosoftLearn().Brave(cloudOnly: true));

    private static IntoChatOSBuilder Os(string topology, params (string Key, string Value)[] settings)
    {
        var args = new List<string>
        {
            $"--IntoChatOS:Topology={topology}",
            "--IntoChatOS:Shell:Enabled=false",
        };
        foreach (var (key, value) in settings)
        {
            args.Add($"--{key}={value}");
        }

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [.. args],
            DisableDashboard = true,
        });
        var profile = DeploymentProfile.From(builder);
        var os = builder.AddIntoChatOS("Modules", IntoChatOSHostingOptions.Named(builder.Configuration, "os"));
        os.Profile = profile;
        return os;
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("clickhouse")]
    [InlineData("github")]
    [InlineData("playwright")]
    [InlineData("artcraft")]
    [InlineData("sandbox")]
    [InlineData("devices")]
    [InlineData("webtools")]
    public void ASwitchedOffModuleGetsNothing(string id)
    {
        var os = Os("cloud", ($"IntoChatOS:Modules:{id}:Enabled", "false"))
            .WithAI(Chain)
            .WithPostgres("intochat_os")
            .WithClickHouse()
            .WithGitHub()
            .WithPlaywright()
            .WithArtCraft()
            .WithSandbox();
        Assert.DoesNotContain(id, os.Modules);
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, r => r.Name.Contains(id, StringComparison.OrdinalIgnoreCase) && r is ContainerResource);
    }

    [Theory]
    [InlineData("devices")]
    [InlineData("webtools")]
    [InlineData("mcp")]
    public void ASwitchedOffLocalAiModuleGetsNothing(string id)
    {
        var os = Os("local", ($"IntoChatOS:Modules:{id}:Enabled", "false"))
            .WithAI(Chain);
        Assert.DoesNotContain(id, os.Modules);
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, r => string.Equals(r.Name, $"Modules-{id}", StringComparison.OrdinalIgnoreCase));
        if (id == "webtools")
        {
            Assert.DoesNotContain(os.ApplicationBuilder.Resources, static r => r.Name.Contains("searxng", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void LocalDefaultsLeaveContainerModulesOut()
        => Assert.Empty(Os("local").WithPostgres("intochat_os").WithClickHouse().WithArtCraft().WithPlaywright().ApplicationBuilder.Resources.OfType<ContainerResource>());

    [Fact]
    public void PostgresGetsItsDatabaseName()
        => Assert.Equal("intochat_os", Os("cloud").WithPostgres("intochat_os").Settings["IntoChatOS:Modules:postgres:Options:hosting:databaseName"]);

    [Fact]
    public void CallingAMemberTwiceThrows()
        => Assert.Throws<InvalidOperationException>(() => Os("cloud").WithGitHub().WithGitHub());
}
