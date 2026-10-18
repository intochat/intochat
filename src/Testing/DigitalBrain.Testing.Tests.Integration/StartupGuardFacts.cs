using System.Reflection;
using Aspire.Hosting.Testing;
using DigitalBrain.Testing.Aspire;
using DigitalBrain.Testing.Playwright;

namespace DigitalBrain.Testing.Tests.Integration;

public sealed class StartupGuardFacts
{
    [Fact]
    public async Task AFailedAspireStartupReleasesWhatItAcquired()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = "marker-" + Guid.NewGuid().ToString("N");
        var options = new TestExecutionOptions { PrivateConfiguration = new Dictionary<string, string?> { ["Parameters:startup-guard"] = marker } };
        // An AppHost with no brain host fails after the private settings file was written.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AspireTestSession.StartCoreAsync(
            _ => Task.FromResult(DistributedApplicationTestingBuilder.Create()), options, ct));
        Assert.Contains("'builder'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "digitalbrain-test-*"),
            directory => File.Exists(Path.Combine(directory, "settings.json"))
                && File.ReadAllText(Path.Combine(directory, "settings.json")).Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidBrowserOptionsFailBeforeTheBrainIsTouched()
    {
        var brain = DispatchProxy.Create<ITestBrain, UntouchableBrain>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            brain.OpenBrowserAsync(new BrowserOptions { SlowMoMilliseconds = -1 }, TestContext.Current.CancellationToken));
    }

    public class UntouchableBrain : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("The brain was touched: " + targetMethod?.Name);
    }
}
