using System.ComponentModel.DataAnnotations;
using DigitalBrain.Modules.Options;

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

    [Range(1, 1440)]
    public int TokenTtlMinutes { get; set; } = 30;

    public void Validate()
    {
        if (TokenTtlMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(TokenTtlMinutes), TokenTtlMinutes, "TokenTtlMinutes must be between 1 and 1440.");
        }
    }
}
