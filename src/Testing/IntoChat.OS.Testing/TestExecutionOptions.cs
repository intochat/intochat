using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;

namespace IntoChat.OS.Testing;

public sealed record TestExecutionOptions
{
    public string? ServerResourceName { get; init; }
    public bool EnableDashboardTelemetry { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan AssertionTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public Action<TestDiagnostic>? Diagnostics { get; init; }
    // False starts an in-process host without Kestrel or the server's endpoints: same silo and services, no HttpClient.
    public bool Http { get; init; } = true;
    public string? ArtifactDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> PrivateConfiguration { get; init; } = new Dictionary<string, string?>();
    // Feeds the AppHost builder's own configuration (not a resource's environment): hosting-layer
    // knobs like IntoChatOS:Modules:flutter:Hosting:WebContainer describe the machine running the AppHost.
    public IReadOnlyDictionary<string, string?> HostConfiguration { get; init; } = new Dictionary<string, string?>();

    public void Validate()
    {
        foreach (var key in PrivateConfiguration.Keys)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains("__", StringComparison.Ordinal)
                || key.StartsWith("Orleans:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(ModuleSettingsValidation.TestingSectionPrefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(IntoChatOSConfiguration.ModulesSectionName + ":", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("Private settings cannot override host-owned configuration."); }
        }
        foreach (var timeout in new[] { StartupTimeout, AssertionTimeout, CleanupTimeout })
        {
            ValidateTimeout(timeout);
        }
    }

    public static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        { throw new ArgumentOutOfRangeException(nameof(timeout), "Test deadlines must be finite and positive."); }
    }
}

public sealed record TestDiagnostic(string Stage, string Message);
