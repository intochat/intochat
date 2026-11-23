using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    private sealed class AIHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        // Pinned: the image the container path was last verified against (ollama/ollama 0.40.6).
        internal const string OllamaImageTag = "0.40.6";

        private readonly HashSet<ModelId> _models = [];
        private readonly Dictionary<ModelId, IResourceBuilder<OllamaModelResource>> _ollamaModels = [];
        private readonly Dictionary<(Where Map, Role Role), ModelId> _roles = [];
        private IResourceBuilder<OllamaResource>? _ollama;

        internal bool EnableSensitiveData { get; set; }

        internal void AddModel(ModelCard card)
        {
            ArgumentNullException.ThrowIfNull(card);
            if (!_models.Add(card.Id))
            {
                return;
            }

            if (card.Provider == AiProvider.Ollama)
            {
                _ollamaModels[card.Id] = EnsureOllama().AddModel(brain.ResourceName(OllamaResourceName(card.Id.Value)), card.Id.Value);
            }
        }

        internal void SetRoles(AIRoleOptions roles)
        {
            ArgumentNullException.ThrowIfNull(roles);
            var map = roles.ToRoleMap();
            foreach (var (role, id) in map.Device)
            {
                _roles[(Where.Device, role)] = id;
            }

            foreach (var (role, id) in map.Cloud)
            {
                _roles[(Where.Cloud, role)] = id;
            }
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithEnvironment(
                EnableSensitiveDataEnvironmentKey,
                EnableSensitiveData ? "true" : "false")
                .WithEnvironment("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT",
                    EnableSensitiveData ? "true" : "false");

            if (_ollama is not null && _ollamaModels.Count == 0)
            {
                builder
                    .WithAnnotation(new WaitAnnotation(_ollama.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                    .WithEnvironment(ModuleConfiguration.EnvironmentName(DevicesHostingExtensions.OllamaEndpointKey), _ollama.Resource.UriExpression);
            }

            foreach (var (modelId, resource) in _ollamaModels)
            {
                builder
                    .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                    .WithEnvironment(ModuleConfiguration.EnvironmentName(DevicesHostingExtensions.OllamaEndpointKey), resource.Resource.Parent.UriExpression)
                    .WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.OllamaModels, $"{modelId.Value}:Model"), resource.Resource.ModelName);
            }

            foreach (var ((where, role), modelId) in _roles)
            {
                builder.WithEnvironment(ModuleConfiguration.EnvironmentName(AIKeys.Role(where, role)), modelId.Value);
            }
        }

        internal IResourceBuilder<OllamaModelResource> AddOllamaBackingModel(string id)
            => EnsureOllama().AddModel(brain.ResourceName(OllamaResourceName(id)), id);

        private static string OllamaResourceName(string id)
            => id.ToLowerInvariant().Replace(':', '-').Replace('.', '-').Replace('/', '-');

        internal IResourceBuilder<OllamaResource> EnsureOllama()
            => _ollama ??= AddOllama();

        internal static bool IsHostOllamaListening(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var connect = tcp.ConnectAsync(uri.Host, uri.Port);
                return connect.Wait(TimeSpan.FromMilliseconds(200)) && tcp.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly Lazy<bool> NvidiaGpuAvailable = new(DetectNvidiaGpu);

        private IResourceBuilder<OllamaResource> AddOllama()
        {
            var ollama = brain.ApplicationBuilder
                .AddOllama(brain.ChildName("ai", "ollama", brain.ResourceName("ollama")))
                .WithImageTag(OllamaImageTag);
            if (NvidiaGpuAvailable.Value)
            {
                ollama = ollama.WithGPUSupport();
            }
            return ollama
                .WithEndpoint("http", static e => e.Port = 11434)
                .WithDataVolume(LegacyNames.DataVolume(brain.ApplicationBuilder, LegacyNames.Resource("ollama")))
                .WithLifetime(ContainerLifetime.Persistent)
                .WithLegacyContainerCleanup(brain.ApplicationBuilder)
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
        IntoChatOSBuilder brain) : IntoChatOSModuleProjection
    {
        private ModelCard? _card;

        internal void SetModel(ModelCard card)
        {
            ArgumentNullException.ThrowIfNull(card);
            if (_card is not null && _card.Id != card.Id)
            {
                throw new InvalidOperationException(
                    $"Voice-to-text is already configured on brain '{brain.Name}' as '{_card.Id}'. "
                    + "Configure Role.Hear once.");
            }

            _card = card;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_card is null || _card.Where != Where.Device)
            {
                return;
            }

            var config = brain.ApplicationBuilder.Configuration;
            var devicesSection = ModuleConfiguration.ModuleSection("devices");
            if (!LocalRunDefaults.IsEnabled(config, "devices")
                || config.GetValue<bool>(devicesSection + ":ReplaceOptions"))
            {
                return;
            }

            var modelId = _card.Id.Value;
            var prefix = ModuleConfiguration.EnvironmentName(DevicesHostingExtensions.PrefetchSectionKey) + "__";
            builder.WithEnvironment(context =>
            {
                foreach (var kv in context.EnvironmentVariables)
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(kv.Value?.ToString(), modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                var index = 0;
                while (context.EnvironmentVariables.ContainsKey($"{prefix}{index}"))
                {
                    index++;
                }

                context.EnvironmentVariables[$"{prefix}{index}"] = modelId;
            });
        }
    }
}
