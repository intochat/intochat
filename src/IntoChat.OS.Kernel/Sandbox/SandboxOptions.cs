using System.ComponentModel.DataAnnotations;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Execution;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>Configures communication between the OS Kernel and the hardened container sandbox.</summary>
public sealed class SandboxOptions : IModuleOptions
{
    public const string SectionName = "sandbox";

    public string? Url { get; set; }
    public string? EnrollKey { get; set; }
    public string? TokenSigningMaterial { get; set; }
    public string? KernelCallbackUrl { get; set; }
    public string? UnitHostAssemblyPath { get; set; }

    /// <summary>
    /// Explicitly permits loopback (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>) HTTP/HTTPS targets for
    /// remote sandbox callback forwarding in local development and E2E tests. Defaults to <see langword="false"/>
    /// so production only forwards callbacks to public <c>https://</c> origins.
    /// </summary>
    public bool AllowLoopbackKernelCallback { get; set; }

    /// <summary>
    /// Enables the local out-of-process <c>IntoChat.OS.Sandbox --unit-host</c> fallback when no connected
    /// sovereign client device is available. Set to <see langword="false"/> on headless cloud servers to
    /// route directly to ACA Dynamic Sessions when configured.
    /// </summary>
    public bool EnableLocalSandbox { get; set; } = true;

    /// <summary>
    /// Headless cloud fallback over Azure Container Apps Dynamic Sessions (Kata MicroVMs).
    /// Remains dormant unless <see cref="AcaDynamicSessionsOptions.PoolManagementEndpoint"/> is configured.
    /// </summary>
    public AcaDynamicSessionsOptions AcaDynamicSessions { get; set; } = new();

    [Range(1, 1440)]
    public int TokenTtlMinutes { get; set; } = 30;

    public void Validate()
    {
        if (TokenTtlMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(TokenTtlMinutes), TokenTtlMinutes, "TokenTtlMinutes must be between 1 and 1440.");
        }

        AcaDynamicSessions ??= new AcaDynamicSessionsOptions();
        AcaDynamicSessions.Validate();
    }
}
