using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddIntoChatOSPipelines();

// The deployment. IntoChatOS:Topology picks the side: local → Device, cloud → Cloud, local+cloud → both.
// IntoChatOS:Modules:<id>:Enabled=false switches a module off; IntoChatOS:Modules:<id>:Options:* overrides what is stated here.
builder.AddIntoChatOS("Modules")
    .WithAI(ai => ai
        .Device(d => d.Ollama().FoundryLocal().Attach("lmstudio", new Uri("http://localhost:1234/v1"))
                      .Roles(r => r.Chat(KnownModels.Qwen35).Think(KnownModels.Qwen25Coder14B).Hear(KnownModels.WhisperLargeV3Turbo).Speak(KnownModels.Kokoro82M))
                      .Prefetch(KnownModels.Qwen35))
        .Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash).Think(KnownModels.Gpt56Luna).Embed(KnownModels.TextEmbedding3Small).Speak(KnownModels.Gpt4oMiniTts).Talk(KnownModels.GptRealtime)))
        .Web(w => w.SearXng().MicrosoftLearn().Brave(cloudOnly: true)))
    .WithPostgres("intochat_os").WithGitHub().WithPlaywright()
    .WithClickHouse().WithArtCraft().WithSandbox()
    .AddKernel("../IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj");

builder.Build().Run();
