namespace IntoChat.Aspire;

// This process's own Aspire resource name. Aspire names each instance's telemetry after its resource, so OTEL_SERVICE_NAME is it;
// null when the process does not run under an AppHost. The one place the module reads it.
internal sealed record AspireInstance(string? Name)
{
    public static AspireInstance FromEnvironment() => new(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"));
}
