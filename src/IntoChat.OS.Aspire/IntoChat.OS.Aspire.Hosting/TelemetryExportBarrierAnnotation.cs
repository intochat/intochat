using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

// A resource that supports the opt-in process-local export barrier. No capability is
// supplied in normal deployments; a test session creates and owns that capability.
public sealed class TelemetryExportBarrierAnnotation : IResourceAnnotation;
