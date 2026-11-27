using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddIntoChatOSPipelines();

// The deployment. IntoChatOS:Topology picks the side: local → Device, cloud → Cloud, local+cloud → both.
// IntoChatOS:Modules:<id>:Enabled=false switches a module off; IntoChatOS:Modules:<id>:Options:* overrides what is stated here.
builder.AddIntoChatOS("Modules")
    .WithAI(ai => ai
        .Device(d => d.Ollama().FoundryLocal().Attach("lmstudio", new Uri("http://localhost:1234/v1"))
                      .Roles(r => r.Chat(KnownModels.Gemma412B).Think(KnownModels.Gemma412B).Hear(KnownModels.WhisperLargeV3Turbo).Speak(KnownModels.Kokoro82M).Embed(KnownModels.EmbeddingGemma))
                      .Prefetch(KnownModels.Gemma412B, KnownModels.WhisperLargeV3Turbo, KnownModels.Kokoro82M, KnownModels.EmbeddingGemma))
        .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash).Think(KnownModels.Gpt56Luna).Embed(KnownModels.TextEmbedding3Small).Speak(KnownModels.Gpt4oMiniTts).Talk(KnownModels.GptRealtime)))
        .Web(w => w.SearXng().MicrosoftLearn().Brave(cloudOnly: true)))
    .WithInfrastructure(infra => infra
        .Postgres(ResourceBacking.LocalContainer)
        .ClickHouse(ResourceBacking.LocalContainer)
        .Playwright(ResourceBacking.LocalContainer)
        .ArtCraft(ResourceBacking.LocalContainer)
        .Sandbox(ResourceBacking.LocalContainer))
    .AddKernel("../IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj");

builder.Build().Run();
