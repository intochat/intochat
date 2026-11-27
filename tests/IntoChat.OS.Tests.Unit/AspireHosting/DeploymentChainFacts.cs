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
        return builder.AddIntoChatOS("Modules");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("clickhouse")]
    [InlineData("playwright")]
    [InlineData("artcraft")]
    [InlineData("sandbox")]
    [InlineData("devices")]
    [InlineData("webtools")]
    public void ASwitchedOffModuleGetsNothing(string id)
    {
        var os = Os("cloud", ($"IntoChatOS:Modules:{id}:Enabled", "false"))
            .WithAI(Chain)
            .WithInfrastructure(infra => infra
                .Postgres(ResourceBacking.LocalContainer)
                .ClickHouse(ResourceBacking.LocalContainer)
                .Playwright(ResourceBacking.LocalContainer)
                .ArtCraft(ResourceBacking.LocalContainer)
                .Sandbox(ResourceBacking.LocalProcess));
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
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, r => string.Equals(r.Name, ResourceNames.Shown(id), StringComparison.OrdinalIgnoreCase));
        if (id == "webtools")
        {
            Assert.DoesNotContain(os.ApplicationBuilder.Resources, static r => r.Name.Contains("searxng", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void LocalDefaultsLeaveContainerModulesOut()
        => Assert.Empty(Os("local")
            .WithInfrastructure(infra => infra
                .Postgres(ResourceBacking.LocalContainer)
                .ClickHouse(ResourceBacking.LocalContainer)
                .ArtCraft(ResourceBacking.LocalContainer)
                .Playwright(ResourceBacking.LocalContainer))
            .ApplicationBuilder.Resources.OfType<ContainerResource>());

    [Fact]
    public void PostgresDerivesDeterministicBrainDatabaseNameByDefault()
    {
        var os = Os("cloud").WithInfrastructure(infra => infra.Postgres(ResourceBacking.LocalContainer));
        var dbName = os.Settings["IntoChatOS:Modules:postgres:Options:hosting:databaseName"];
        Assert.Equal(InfrastructureBackingBuilder.DerivePostgresDatabaseName(os.ServiceId), dbName);
        Assert.StartsWith("brain_", dbName, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceBackingOffProvisionsNothing()
    {
        var os = Os("cloud").WithInfrastructure(infra => infra
            .Postgres(ResourceBacking.Off)
            .ClickHouse(ResourceBacking.Off)
            .ArtCraft(ResourceBacking.Off)
            .Sandbox(ResourceBacking.Off));
        Assert.DoesNotContain("postgres", os.Modules);
        Assert.DoesNotContain("clickhouse", os.Modules);
        Assert.DoesNotContain("artcraft", os.Modules);
        Assert.DoesNotContain("sandbox", os.Modules);
    }

    [Fact]
    public void ResourceBackingCloudBindsExternalParameterWithoutLocalPostgresContainer()
    {
        var os = Os("cloud").WithInfrastructure(infra => infra.Postgres(ResourceBacking.Cloud));
        Assert.Contains("postgres", os.Modules);
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, static r => r is PostgresServerResource);
        Assert.Contains(os.ApplicationBuilder.Resources, static r => r is ParameterResource && r.Name.Contains("postgres-connection", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyObsoleteShimsRemainCompatibleForExternalHosts()
    {
#pragma warning disable CS0618
        var os = Os("cloud").WithPostgres("intochat_os").WithGitHub();
        Assert.Equal("intochat_os", os.Settings["IntoChatOS:Modules:postgres:Options:hosting:databaseName"]);
        Assert.Contains("github", os.Modules);
        Assert.Throws<InvalidOperationException>(() => Os("cloud").WithGitHub().WithGitHub());
#pragma warning restore CS0618
    }
}
