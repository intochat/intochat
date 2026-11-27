using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class AppHostChainFacts
{
    private static readonly string KernelPath = DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel");

    internal static IntoChatOSBuilder Compose(string topology, params (string Key, string Value)[] settings)
    {
        var args = new List<string>
        {
            $"--IntoChatOS:Topology={topology}",
            "--IntoChatOS:Shell:Enabled=false",
            // Whether a host Ollama answers is the developer's machine, not the composition under test.
            "--IntoChatOS:Ollama:AutoContainer=false",
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

        var silos = builder.AddIntoChatOS("Modules")
            .WithAI(ai => ai
                // .Device(d => d.Ollama().FoundryLocal().Attach("lmstudio", new Uri("http://localhost:1234/v1"))
                //               .Roles(r => r.Chat(KnownModels.Gemma412B).Think(KnownModels.Gemma412B).Hear(KnownModels.WhisperLargeV3Turbo).Speak(KnownModels.Kokoro82M).Embed(KnownModels.EmbeddingGemma))
                //               .Prefetch(KnownModels.Gemma412B, KnownModels.WhisperLargeV3Turbo, KnownModels.Kokoro82M, KnownModels.EmbeddingGemma))
                .Cloud(c => c.AzureFoundry()
                             .Roles(r => r.Chat(KnownModels.DeepSeekV41Flash)
                                          .Fast(KnownModels.Gpt54Nano)
                                          .Think(KnownModels.Gpt56Sol)
                                          .Hear(KnownModels.Gpt4oTranscribe)
                                          .Speak(KnownModels.Gpt4oMiniTts)
                                          .Talk(KnownModels.GptRealtime)
                                          .Embed(KnownModels.TextEmbedding3Small)
                                          .Image(KnownModels.GptImage1)))
                .Web(w => w.MicrosoftLearn().Brave()))
            .WithInfrastructure(infra => infra
                .Postgres(ResourceBacking.LocalContainer)
                .ClickHouse(ResourceBacking.LocalContainer)
                .Playwright(ResourceBacking.LocalContainer)
                .ArtCraft(ResourceBacking.LocalContainer)
                .Sandbox(ResourceBacking.LocalContainer))
            .AddKernel(KernelPath);

        return silos[0].Resource.Annotations.OfType<BrainSiloAnnotation>().Single().Brain;
    }

    private static IResource Kernel(IntoChatOSBuilder os)
        => Assert.Single(os.ApplicationBuilder.Resources, static r => r.Name == "os");

    private static async Task<Dictionary<string, object>> KernelEnvironment(IntoChatOSBuilder os)
    {
        var kernel = Kernel(os);
        var env = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var context = new EnvironmentCallbackContext(os.ApplicationBuilder.ExecutionContext, kernel, env, TestContext.Current.CancellationToken);
        foreach (var callback in kernel.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        return env;
    }

    private static IntoChatOSBuilder Brain()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return builder.AddIntoChatOSBrain("Modules", new IntoChatOSHostingOptions());
    }

    private static string AppHostCs()
        => Path.Combine(Path.GetDirectoryName(DeploymentSnapshot.ProjectPath("IntoChat.OS.AppHost"))!, "AppHost.cs");

    private static string ThisFile()
        => Path.Combine(Path.GetDirectoryName(DeploymentSnapshot.ProjectPath("IntoChat.OS.Tests.Unit"))!, "AspireHosting", "AppHostChainFacts.cs");

    private static string ChainText(string path)
    {
        var content = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = content.IndexOf("builder.AddIntoChatOS(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing 'builder.AddIntoChatOS(' in {path}.");
        var end = content.IndexOf(".AddKernel(", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing '.AddKernel(' after 'builder.AddIntoChatOS(' in {path}.");

        var slice = content[start..end];
        return string.Join('\n', slice.Split('\n').Select(static line => line.Trim()));
    }

    [Fact]
    public void TheFactsComposeWhatAppHostSays()
        => Assert.Equal(ChainText(AppHostCs()), ChainText(ThisFile()));

    [Fact]
    public void ConfigurationBeatsTheChain()
        => Assert.Equal(
            "DeepSeek-R1",
            Compose("local", ("IntoChatOS:Modules:ai:Options:Roles:Cloud:Chat", "DeepSeek-R1"))
                .Settings["IntoChatOS:Modules:ai:Options:Roles:Cloud:Chat"]);

    [Fact]
    public async Task TheLocalKernelHearsTheCloudFoundryRolesWithoutDevice()
    {
        var env = await KernelEnvironment(Compose("local"));
        Assert.Equal("deepseek/deepseek-v4.1-flash", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Chat"]);
        Assert.Equal("gpt-5.4-nano", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Fast"]);
        Assert.Equal("gpt-5.6-sol", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Think"]);
        Assert.Equal("gpt-4o-transcribe", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Hear"]);
        Assert.Equal("gpt-4o-mini-tts", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Speak"]);
        Assert.Equal("gpt-realtime", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Talk"]);
        Assert.Equal("text-embedding-3-small", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Embed"]);
        Assert.Equal("gpt-image-1", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Image"]);
        Assert.DoesNotContain(env.Keys, static k => k.StartsWith("IntoChatOS__Modules__ai__Options__Roles__Device__", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheCloudKernelHearsTheCloudVoiceRoles()
    {
        var env = await KernelEnvironment(Compose("cloud"));
        Assert.Equal("gpt-4o-mini-tts", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Speak"]);
        Assert.Equal("gpt-realtime", env["IntoChatOS__Modules__ai__Options__Roles__Cloud__Talk"]);
        Assert.DoesNotContain(env.Keys, static k => k.StartsWith("IntoChatOS__Modules__ai__Options__Roles__Device__", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalKernelWithoutDeviceHasNoInstallModelCommand()
        => Assert.DoesNotContain(Kernel(Compose("local")).Annotations.OfType<ResourceCommandAnnotation>(), static c => c.Name == "install-model");

    [Fact]
    public void CloudKernelHasNoInstallModelCommand()
        => Assert.DoesNotContain(Kernel(Compose("cloud")).Annotations.OfType<ResourceCommandAnnotation>(), static c => c.Name == "install-model");

    [Fact]
    public void AddKernelWithoutTheDeploymentSaysWhy()
        => Assert.Contains("AddIntoChatOS(name)", Assert.Throws<InvalidOperationException>(() => Brain().AddKernel("x.csproj")).Message);

    [Fact]
    public void TheOldWayIsGone()
    {
        Assert.Null(typeof(DeploymentProfile).GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(typeof(DeploymentProfile).GetMethod("LocalAi", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(typeof(DeploymentProfile).GetMethod("CloudAi", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }
}
