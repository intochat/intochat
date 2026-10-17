using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.SystemOne;
using DigitalBrain.Aspire.Hosting;

using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.AI.Aspire.Hosting;

public static partial class AIHostingExtensions
{
    public const string SystemOneResourceName = "systemone";
    // The adapter checks no key; the integration still needs one to be Ready.
    public const string SystemOneApiKeyLocal = "local";
    private const string SystemOneAdapterDirectory = "src/DigitalBrain.Modules/AI/systemone-adapter";

    // "typesafe" mirrors the module's internal HttpSystemOne.IntegrationId.
    private static readonly string TypeSafeEndpointEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("typesafe", "Endpoint"));
    private static readonly string TypeSafeApiKeyEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("typesafe", "ApiKey"));
    private static readonly string DefaultSystemOneEnvironmentKey =
        DigitalBrainConfiguration.EnvironmentName(AIKeys.DefaultSystemOne);

    private sealed class SystemOneHostingState(
        DigitalBrainBuilder brain,
        IResourceBuilder<DigitalBrainModuleResource> module) : DigitalBrainModuleProjection
    {
        private SystemOneModel? _default;
        private IResourceBuilder<OllamaModelResource>? _backing;
        private IResourceBuilder<ContainerResource>? _adapter;

        internal void SetDefault(SystemOneModel model, IResourceBuilder<OllamaModelResource>? backing)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (_default is not null && !string.Equals(_default.Id, model.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(

                    $"System One is already configured on brain '{brain.Name}' as '{_default.Id}'. Call WithDefaultSystemOne once.");
            }
            _default = model;
            _backing = backing;
        }

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_default is null) { return; }
            builder.WithEnvironment(DefaultSystemOneEnvironmentKey, _default.Marker.Name);
            if (_default.Provider is not AiProvider.Ollama) { return; }

            _adapter ??= AddAdapter();
            builder
                .WithAnnotation(new WaitAnnotation(_adapter.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                .WithEnvironment(TypeSafeEndpointEnvironmentKey, _adapter.GetEndpoint("http"))
                .WithEnvironment(TypeSafeApiKeyEnvironmentKey, SystemOneApiKeyLocal);
        }

        // The adapter image builds from the repository's systemone-adapter folder; a host composed
        // outside the repository cannot run a local Jev.

        private IResourceBuilder<ContainerResource> AddAdapter()
        {
            var repository = RepositoryRoot() ?? throw new InvalidOperationException(
                "WithDefaultSystemOne<ILocalJev>() builds the adapter from the repository; run the AppHost from inside it or use IJev.");
            var backing = _backing ?? throw new InvalidOperationException("A local System One needs its Ollama model.");
            var ollama = backing.Resource.Parent;

            return brain.ApplicationBuilder
                .AddDockerfile(SystemOneResourceName, Path.Combine(repository, SystemOneAdapterDirectory))
                .WithHttpEndpoint(targetPort: 8000, name: "http")
                .WithHttpHealthCheck("/health")
                .WithEnvironment("OPENAI_BASE_URL", ReferenceExpression.Create($"{ollama.UriExpression}/v1"))
                .WithEnvironment("OPENAI_API_KEY", "not-needed")
                .WithEnvironment("SYSTEMONE_MODEL", _default!.Id)
                .WithAnnotation(new WaitAnnotation(backing.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                .WithParentRelationship(module);
        }

        private static string? RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            { if (File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx"))) { return directory.FullName; } }
            return null;
        }
    }
}
