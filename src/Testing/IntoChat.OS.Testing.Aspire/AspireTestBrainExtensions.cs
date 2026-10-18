using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Testing.Aspire.Diagnostics;

namespace IntoChat.OS.Testing.Aspire;

public static class AspireTestBrainExtensions
{
    public static TestBrain WithDashboardTelemetry(this TestBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain.ConfigureExecution(options => options with { EnableDashboardTelemetry = true });
    }

    public static TestTelemetry Telemetry(this ITestBrain brain) => AspireBrainExtensions.Of(brain).Session.Telemetry
        ?? throw new InvalidOperationException("Enable WithDashboardTelemetry on this host before querying telemetry.");

    public static TestBrain InEnvironment(this TestBrain brain, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain.WithHostConfiguration(IntoChatOSHostingOptions.EnvironmentKey, environmentName);
    }
}
