using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Microsoft.Aspire;

// Candidates work in {repository}/.digitalbrain (gitignored): worktrees, builds and the candidate Dockerfile.
public sealed class AspireModuleHosting : ModuleHosting<DigitalBrain.Microsoft.Aspire.AspireModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var repository = Path.GetFullPath(Path.Combine(brain.ApplicationBuilder.AppHostDirectory, "..", ".."));
        brain.AddProjection(new Workdir(Path.Combine(repository, ".digitalbrain")));
    }

    private sealed class Workdir(string path) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(global::Aspire.Hosting.ApplicationModel.IResourceBuilder<TResource> builder)
            => global::Aspire.Hosting.ResourceBuilderExtensions.WithEnvironment(builder,
                DigitalBrainConfiguration.EnvironmentName(AspireModule.Integration.Key("Workdir")), path);
    }
}
