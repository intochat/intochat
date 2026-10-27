using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.Ollama;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.AI.Tests.Hosting;

// A host without an NVIDIA runtime must still start the Ollama container: GPU passthrough is opt-in
// (IntoChatOS:Local:Gpu, set from the launcher's detection) and the image is pinned.
public sealed class OllamaGpuFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheContainerPathPassesTheGpuOnlyWhenTheHostAsksForIt(bool gpu)
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [IntoChatOSHostingOptions.LocalGpuKey] = gpu ? "true" : "false" });
        builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultLlm<IQwen35>().WithoutWebSearch());

        var ollama = Assert.Single(builder.Resources.OfType<OllamaResource>());
        var arguments = new List<object>();
        var context = new ContainerRuntimeArgsCallbackContext(arguments, TestContext.Current.CancellationToken);
        foreach (var callback in ollama.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()) { await callback.Callback(context); }
        Assert.Equal(gpu, arguments.Any(argument => argument.ToString()!.Contains("gpus", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheOllamaImageIsPinnedToAVersion()
    {
        var builder = CreateBuilder();
        builder.AddIntoChatOS("brain", new())
            .WithHosting<AIModuleHosting, AIOptions>(ai => ai.WithDefaultLlm<IQwen35>().WithoutWebSearch());
        var image = Assert.Single(Assert.Single(builder.Resources.OfType<OllamaResource>()).Annotations.OfType<ContainerImageAnnotation>());
        Assert.NotEqual("latest", image.Tag);
        Assert.Matches(@"^\d+\.\d+\.\d+$", image.Tag!);
    }
}
