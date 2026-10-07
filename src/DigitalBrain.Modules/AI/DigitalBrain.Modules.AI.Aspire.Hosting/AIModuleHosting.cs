using DigitalBrain.AI.Models;
using DigitalBrain.AI.Voice;
using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.AI.Aspire.Hosting;

public sealed class AIModuleHosting : ModuleHosting<DigitalBrain.AI.AIModule>
{
    public override void Configure(DigitalBrainBuilder brain)
        => AIHostingExtensions.ConfigureDeclaration(brain,
            brain.GetModuleConfiguration("ai").GetModuleOptions<AIOptions>("ai"));
}

public static partial class AIHostingExtensions
{
    internal static void ConfigureDeclaration(DigitalBrainBuilder brain, AIOptions options)
    {
        var module = new DigitalBrainModuleBuilder<DigitalBrain.AI.Aspire.Hosting.AIModuleHosting>(brain);
        var state = State(module);
        state.EnableSensitiveData = options.Telemetry.EnableSensitiveData ?? false;
        foreach (var name in options.Hosting.Llms)
        {
            var model = LLMModel.FindByMarkerName(name) ?? throw new InvalidOperationException(
                $"'{name}' is not a catalogued LLM. Known: {string.Join(", ", LLMModel.All.Select(static m => m.Marker.Name))}.");
            state.AddLlm(model.Marker);
        }
        foreach (var name in options.Hosting.Embeddings)
        {
            var model = EmbeddingModel.FindByMarkerName(name) ?? throw new InvalidOperationException(
                $"'{name}' is not a catalogued embedding model. Known: {string.Join(", ", EmbeddingModel.All.Select(static m => m.Marker.Name))}.");
            state.AddEmbedding(model.Marker);
        }
        if (options.Default.Transcription is { Length: > 0 } nameOfVoice)
        {
            var model = TranscriptionModel.FindByMarkerName(nameOfVoice) ?? throw new InvalidOperationException(
                $"'{nameOfVoice}' is not a catalogued transcription model. Known: {string.Join(", ", TranscriptionModel.All.Select(static m => m.Marker.Name))}.");
            var voice = module.GetOrAddProjection(b => new VoiceToTextHostingState(b, module.Resource));
            if (!model.IsLocal) { state.EnsureProviderApiKey(model.Provider); }
            voice.SetModel(model);
        }
        if (options.Tavily.Enabled) { state.EnableTavilySearch(); }
    }
}
