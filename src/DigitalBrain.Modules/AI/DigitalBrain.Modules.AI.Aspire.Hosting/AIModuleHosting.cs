using DigitalBrain.AI.Models;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.AI.Voice;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using Microsoft.Extensions.Configuration;

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
            voice.SetModel(model);
        }
        foreach (var name in options.Hosting.SystemOnes)
        {
            var model = SystemOneModel.FindByMarkerName(name) ?? throw new InvalidOperationException(
                $"'{name}' is not a catalogued System One model. Known: {string.Join(", ", SystemOneModel.All.Select(static m => m.Marker.Name))}.");
            if (!string.Equals(options.Default.SystemOne, name, StringComparison.Ordinal)) { continue; }
            var systemOne = module.GetOrAddProjection(b => new SystemOneHostingState(b, module.Resource));
            if (model.Provider is AiProvider.Ollama) { systemOne.SetDefault(model, state.AddOllamaBackingModel(model.Id)); }
            else { systemOne.SetDefault(model, null); }
        }
    }
}
