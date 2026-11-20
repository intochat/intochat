using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    private sealed class AIHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        // Pinned: the image the container path was last verified against (ollama/ollama 0.40.6).
        internal const string OllamaImageTag = "0.40.6";

        private readonly HashSet<Type> _markers = [];
        private readonly Dictionary<Type, IResourceBuilder<OllamaModelResource>> _ollamaModels = [];
        private IResourceBuilder<OllamaResource>? _ollama;
        private Type? _defaultLlmMarker;
        private Type? _defaultEmbeddingMarker;

        internal bool EnableSensitiveData { get; set; }

        internal void AddLlm(Type marker)
        {
            var model = LLMModel.FindByMarker(marker)
                ?? throw new NotSupportedException(
                    $"{marker.FullName} is not a known LLM model marker. Add it to LLMModel.All first.");

            AddModel(marker, model.Provider, model.Id);
        }

        internal void AddEmbedding(Type marker)
        {
            var model = EmbeddingModel.FindByMarker(marker)
                ?? throw new NotSupportedException(
                    $"{marker.FullName} is not a known embedding model marker. Add it to EmbeddingModel.All first.");

            AddModel(marker, model.Provider, model.Id);
        }

        internal void SetDefaultLlm(Type marker)
        {
            RequireAdded(marker);
            _defaultLlmMarker = marker;
        }

        internal void SetDefaultEmbedding(Type marker)
        {
            RequireAdded(marker);
            _defaultEmbeddingMarker = marker;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithEnvironment(
                EnableSensitiveDataEnvironmentKey,
                EnableSensitiveData ? "true" : "false")
                // Keep standard SDK instrumentation and the module pipeline on one
                // explicit host setting; an explicit false overrides ambient opt-ins.
                .WithEnvironment("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT",
                    EnableSensitiveData ? "true" : "false");

            foreach (var (marker, resource) in _ollamaModels)
            {
                builder
                    .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                    .WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.OllamaEndpoint), resource.Resource.Parent.UriExpression)
                    .WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.OllamaModels, $"{marker.Name}:Model"), resource.Resource.ModelName);
            }

            if (_defaultLlmMarker is { } llmMarker)
            {
                builder.WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.DefaultModel), llmMarker.Name);
            }

            if (_defaultEmbeddingMarker is { } embeddingMarker)
            {
                builder.WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.DefaultEmbedding), embeddingMarker.Name);
            }
        }

        private void AddModel(Type marker, AiProvider provider, string id)
        {
            if (!_markers.Add(marker))
            {
                throw new InvalidOperationException(
                    $"{marker.FullName} is already configured on brain '{brain.Name}'. Add each model exactly once.");
            }

            if (provider == AiProvider.Ollama)
            {
                _ollamaModels[marker] = EnsureOllama().AddModel(brain.ResourceName(OllamaResourceName(id)), id);
            }
        }

        // An Ollama model that backs something other than an LLM/embedding neuron (the System One
        // adapter). Not a marker: nothing is keyed by it, so it is not added to _markers.
        internal IResourceBuilder<OllamaModelResource> AddOllamaBackingModel(string id)
            => EnsureOllama().AddModel(brain.ResourceName(OllamaResourceName(id)), id);

        private void RequireAdded(Type marker)
        {
            if (!_markers.Contains(marker))
            {
                throw new InvalidOperationException(
                    $"{marker.Name} must be added with WithLlm/WithEmbedding before it can become the default.");
            }
        }

        private static string OllamaResourceName(string id)
            => id.ToLowerInvariant().Replace(':', '-').Replace('.', '-').Replace('/', '-');

        internal IResourceBuilder<OllamaResource> EnsureOllama()
            => _ollama ??= AddOllama();

        private static readonly Lazy<bool> NvidiaGpuAvailable = new(DetectNvidiaGpu);

        private IResourceBuilder<OllamaResource> AddOllama()
        {
            var ollama = brain.ApplicationBuilder
                .AddOllama(brain.ResourceName("ollama"))
                .WithImageTag(OllamaImageTag);
            if (NvidiaGpuAvailable.Value)
            {
                ollama = ollama.WithGPUSupport();
            }
            return ollama
                .WithDataVolume()
                .WithLifetime(ContainerLifetime.Persistent)
                .WithOpenWebUI(openwebui => openwebui.WithLifetime(ContainerLifetime.Persistent))
                .WithParentRelationship(module);
        }

        private static bool DetectNvidiaGpu()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name --format=csv,noheader")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) { return false; }
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private sealed class VoiceToTextHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        // One key for every transcription model, matching the default model and
        // embedding keys. The provider on the model picks the implementation.
        private static readonly string TranscriptionEnvironmentKey =
            ModuleConfiguration.EnvironmentName(AIKeys.DefaultTranscription);
        private TranscriptionModel? _model;
        private IResourceBuilder<FoundryLocalModelResource>? _whisper;

        internal void SetModel(TranscriptionModel model)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (_model is not null && !string.Equals(_model.Id, model.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Voice-to-text is already configured on brain '{brain.Name}' as '{_model.Id}'. "
                    + "Call WithVoiceToText once.");
            }

            _model = model;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_model is null)
            {
                return;
            }

            builder.WithEnvironment(TranscriptionEnvironmentKey, _model.Marker.Name);

            if (_model.Provider is not AiProvider.FoundryLocal)
            {
                return;
            }

            _whisper ??= brain.ApplicationBuilder.AddFoundryLocalModel(
                brain.ResourceName(ResourceName(_model.Id)),
                _model.Id,
                module.Resource);
            builder.WithAnnotation(new WaitAnnotation(_whisper.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }

        private static string ResourceName(string id)
            => id.ToLowerInvariant().Replace(':', '-').Replace('.', '-').Replace('/', '-');
    }
}
