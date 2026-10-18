using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Microsoft.CSharp;

public sealed class CSharpOptions : IModuleOptions
{
    // The agent tools, the registry entry and the /csharp console routes. The runtime edge and the compile
    // gate the Apps module uses are not authoring and stay on either way.
    public bool Authoring { get; set; } = true;
    // Signals buffered per subscription while nothing drains; past it the oldest is dropped and counted.
    public int MaximumPending { get; set; } = 64;
    // How long an app's rules may take to judge one request; past it the request is rejected.
    public TimeSpan RuleTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPending, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RuleTimeout, TimeSpan.Zero);
    }
}

// Deployment-owned values under the module's section; they are not user options, so they
// live beside Options, not under it.
public sealed class CSharpDeploymentSettings
{
    public static readonly string SectionName = IntoChatOSConfiguration.ModuleSection(new ModuleId("csharp")) + ":Deployment";

    // The runtime edge URL sessions dial; development derives it from this brain's own HTTP address.
    public string? EdgeUrl { get; set; }
    // Production: the Azure Container Apps session pool management endpoint. Set, it replaces the local container.
    public string? SessionPoolEndpoint { get; set; }
    // Development: the always-on csharp-sandbox container that hosts every brain's session.
    public string? SandboxUrl { get; set; }
}
