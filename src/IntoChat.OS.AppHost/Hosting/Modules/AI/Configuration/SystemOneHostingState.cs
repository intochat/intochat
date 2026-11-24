using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    public const string SystemOneResourceName = "systemone";
    // The adapter checks no key; the integration still needs one to be Ready.
    public const string SystemOneApiKeyLocal = "local";
    private const string SystemOneAdapterDirectory = "src/Modules/AI/systemone-adapter";

    // "typesafe" mirrors the module's internal HttpSystemOne.IntegrationId.
    private static readonly string TypeSafeEndpointEnvironmentKey =
        ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("typesafe", "Endpoint"));
    private static readonly string TypeSafeApiKeyEnvironmentKey =
        ModuleConfiguration.EnvironmentName(IntegrationSchema.Key("typesafe", "ApiKey"));

    private sealed class SystemOneHostingState(
        IntoChatOSBuilder brain,
        IResourceBuilder<IntoChatOSModuleResource> module) : IntoChatOSModuleProjection
    {
        private ModelCard? _default;
        private IResourceBuilder<OllamaModelResource>? _backing;
        private IResourceBuilder<ContainerResource>? _adapter;

        internal void SetDefault(ModelCard card, IResourceBuilder<OllamaModelResource>? backing)
        {
            ArgumentNullException.ThrowIfNull(card);
            if (_default is not null && _default.Id != card.Id)
            {
                throw new InvalidOperationException(
                    $"System One is already configured on brain '{brain.Name}' as '{_default.Id}'. Configure ModelRole.Fast once.");
            }
            _default = card;
            _backing = backing;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_default is null) { return; }
            if (_default.Provider is not AiProvider.Ollama) { return; }

            _adapter ??= AddAdapter();
            builder
                .WithAnnotation(new WaitAnnotation(_adapter.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                .WithEnvironment(TypeSafeEndpointEnvironmentKey, _adapter.GetEndpoint("http"))
                .WithEnvironment(TypeSafeApiKeyEnvironmentKey, SystemOneApiKeyLocal);
        }

        private IResourceBuilder<ContainerResource> AddAdapter()
        {
            var repository = RepositoryRoot() ?? throw new InvalidOperationException(
                "Local System One (KnownModels.LocalJev) builds the adapter from the repository; run the AppHost from inside it or use KnownModels.Jev.");
            var adapterPath = Path.Combine(repository, SystemOneAdapterDirectory);
            if (!Directory.Exists(adapterPath))
            {
                var candidate = Path.GetFullPath(Path.Combine(repository, "..", "modules", SystemOneAdapterDirectory));
                if (Directory.Exists(candidate)) { adapterPath = candidate; }
            }
            var backing = _backing ?? throw new InvalidOperationException("A local System One needs its Ollama model.");
            var ollama = backing.Resource.Parent;

            return brain.ApplicationBuilder
                .AddDockerfile(SystemOneResourceName, adapterPath)
                .WithHttpEndpoint(targetPort: 8000, name: "http")
                .WithHttpHealthCheck("/health")
                .WithEnvironment("OPENAI_BASE_URL", ReferenceExpression.Create($"{ollama.UriExpression}/v1"))
                .WithEnvironment("OPENAI_API_KEY", "not-needed")
                .WithEnvironment("SYSTEMONE_MODEL", _default!.Id.Value)
                .WithAnnotation(new WaitAnnotation(backing.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                .WithParentRelationship(module);
        }

        private static string? RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            { if (File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { return directory.FullName; } }
            return null;
        }
    }
}
