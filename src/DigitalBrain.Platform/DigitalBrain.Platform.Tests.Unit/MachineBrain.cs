using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;

namespace DigitalBrain.Platform.Tests.Unit;

internal static class MachineBrain
{
    // The brain runs as the owner's machine: cluster storage on the part log over an in-process mesh of 8
    // platform-owned nodes (4+2, 2 manifest replicas).
    public static TestBrain AsMachine(this TestBrain brain)
        => brain.WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Own))
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", "8")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2");
}
