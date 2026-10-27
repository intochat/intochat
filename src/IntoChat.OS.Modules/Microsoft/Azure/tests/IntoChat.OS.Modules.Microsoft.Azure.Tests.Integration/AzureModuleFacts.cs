using IntoChat.OS.Modules.Microsoft.Azure.Install;

namespace IntoChat.OS.Modules.Microsoft.Azure.Tests.Integration;

// The Azure module's in-process tier (TEST-01): the install plan the neuron applies is deterministic, so the integration tier
// pins it from the module's public surface; ARM itself is exercised only against a real subscription (deferred-live).
public sealed class AzureModuleFacts
{
    [Fact]
    public void TheInstallPlanIsDeterministicForTheSameConfig()
    {
        var config = new InstallConfig { Subscription = "00000000-0000-0000-0000-000000000001", Location = "westeurope", Prefix = "intochat", Env = "test", OsImage = "ghcr.io/intochat/os:1.0.0" };
        Assert.Equal(InstallPlan.Build(config).Select(spec => spec.Name), InstallPlan.Build(config).Select(spec => spec.Name));
    }
}
