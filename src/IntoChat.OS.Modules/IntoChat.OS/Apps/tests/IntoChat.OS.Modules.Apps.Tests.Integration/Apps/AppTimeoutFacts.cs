using System.Reflection;

namespace IntoChat.OS.Apps.Tests.Apps;

// Deploying a behavior runs it in the sandbox, which outlasts Orleans' 30-second default; an
// operation that deploys without a longer timeout fails its caller while the work still finishes.
public sealed class AppTimeoutFacts
{
    [Theory]
    [InlineData(nameof(IApp.WriteBehavior))]
    [InlineData(nameof(IApp.CompleteBuild))]
    [InlineData(nameof(IApp.Configure))]
    [InlineData(nameof(IApp.Import))]
    [InlineData(nameof(IApp.Rollback))]
    [InlineData(nameof(IApp.Delete))]
    public void EveryOperationThatDeploysBehaviorsOutlastsTheDefaultCallTimeout(string operation)
    {
        var timeout = typeof(IApp).GetMethod(operation)!.GetCustomAttribute<ResponseTimeoutAttribute>();
        Assert.NotNull(timeout);
        Assert.True(timeout.Timeout >= TimeSpan.FromMinutes(10), $"{operation} allows only {timeout.Timeout}.");
    }
}
