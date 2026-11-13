using System.Reflection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// Deploying a behavior runs it in the sandbox, which outlasts the runtime's default call timeout; an
// operation that deploys without a longer timeout fails its caller while the work still finishes.
public sealed class AppTimeoutFacts
{
    [Theory]
    [InlineData(nameof(IApp.CompleteBuild))]
    [InlineData(nameof(IApp.Configure))]
    [InlineData(nameof(IApp.Import))]
    [InlineData(nameof(IApp.Rollback))]
    [InlineData(nameof(IApp.Delete))]
    public void EveryOperationThatDeploysBehaviorsOutlastsTheDefaultCallTimeout(string operation)
    {
        // 06-08: the app is a plain neuron; its call timeout is DigitalBrain's [CallTimeout].
        var timeout = typeof(IApp).GetMethod(operation)!.GetCustomAttribute<DigitalBrain.Contracts.CallTimeoutAttribute>();
        Assert.NotNull(timeout);
        Assert.True(timeout.Timeout >= TimeSpan.FromMinutes(10), $"{operation} allows only {timeout.Timeout}.");
    }
}
