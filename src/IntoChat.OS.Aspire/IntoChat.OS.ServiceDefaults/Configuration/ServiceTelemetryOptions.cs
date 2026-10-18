using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.ServiceDefaults;

public sealed class ServiceTelemetryOptions
{
    public const string SectionName = "Telemetry:Tracing";
    public double? SampleRatio { get; set; }
}

// Standard OpenTelemetry keys and the module-specific privacy override stay at this
// composition boundary. The SDK still reads its own exporter configuration.
internal sealed class TelemetryIntegrationOptions
{
    [ConfigurationKeyName("OTEL_EXPORTER_OTLP_ENDPOINT")]
    public string? ExporterEndpoint { get; set; }

    // Mirrors AIKeys.TelemetryEnableSensitiveData; ServiceDefaults is reference-free, so the
    // AI module's key is restated here once, at this composition boundary.
    public const string EnableSensitiveDataKey = "IntoChatOS:Modules:ai:Options:Telemetry:EnableSensitiveData";

    [ConfigurationKeyName(EnableSensitiveDataKey)]
    public bool? EnableSensitiveData { get; set; }
}
