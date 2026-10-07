using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp;

public sealed class CSharpOptions : IModuleOptions
{
    // The repository the sandbox mounts at /brain; scripts compile against its contract projects.
    public string? SourceRoot { get; set; }
    // The IAspire neuron that starts the development sandbox and reports its URL.
    public string AspireApplication { get; set; } = "DigitalBrain";
    // Signals buffered per subscription while nothing drains; past it the oldest is dropped and counted.
    public int MaximumPending { get; set; } = 64;

    public CSharpOptions WithSandbox(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        SourceRoot = Path.GetFullPath(sourceRoot);
        return this;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AspireApplication);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPending, 1);
    }
}

// Deployment-owned values under the module's section; they are not user options, so they
// live beside Options, not under it.
public sealed class CSharpDeploymentSettings
{
    public static readonly string SectionName = DigitalBrainConfiguration.ModuleSection(new ModuleId("csharp")) + ":Deployment";

    // The script edge URL runs dial; development derives it from this brain's own HTTP address.
    public string? EdgeUrl { get; set; }
    // Production: the Azure Container Apps session pool management endpoint. Set, it replaces the Aspire sandbox.
    public string? SessionPoolEndpoint { get; set; }
    // Development: the allocated csharp-sandbox endpoint the AppHost projects for direct health probes.
    public string? SandboxProbeUrl { get; set; }
}
