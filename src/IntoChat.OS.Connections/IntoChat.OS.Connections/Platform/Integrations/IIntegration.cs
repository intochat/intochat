using System.Text;
using IntoChat.OS.Framework.Contracts;
using Orleans.Concurrency;

namespace IntoChat.OS.Framework.Platform.Integrations;

public enum IntegrationStatus
{
    Unconfigured = 0,
    Partial = 1,
    Ready = 2,
}

// Key: "integration/{id}". One deployment-level provider registration (a Google OAuth app, an AI key);
// every value lives in the vault and never appears in state, snapshots or signals.
[Alias("integration.registration")]
[Orleans.Metadata.DefaultGrainType("integration.registration")]
public interface IIntegration : IGrainWithStringKey
{
    [ReadOnly]
    Task<IntegrationSnapshot> Read();

    Task<IntegrationSnapshot> Configure(ConfigureIntegration request);

    Task<IntegrationSnapshot> Clear(string field);

    // Reconciles deployment-provided values: applies a field when it is absent from state, or when the
    // stored value also came from the deployment and now differs. A value a person set or cleared is
    // never touched.
    Task<IntegrationSnapshot> Seed(ConfigureIntegration request);

    Task<ReleasedIntegration> Release(CallerContext caller);
}

[GenerateSerializer, Alias("integration.registration.snapshot")]
public sealed record IntegrationSnapshot
{
    [Id(0)] public string IntegrationId { get; init; } = "";
    [Id(1)] public IntegrationStatus Status { get; init; }
    [Id(2)] public string[] MissingFields { get; init; } = [];
    [Id(3)] public Dictionary<string, string> Settings { get; init; } = [];
    [Id(4)] public long Revision { get; init; }
}

// Field name to value. The values go to the vault immediately, and the record never prints them.
[GenerateSerializer, Alias("integration.registration.configure")]
public sealed record ConfigureIntegration
{
    [Id(0)] public Dictionary<string, string> Values { get; init; } = [];

    private bool PrintMembers(StringBuilder builder) => builder.Append("Values = <redacted>") is not null;
}

// Handed only to trusted platform code; never persisted.
[GenerateSerializer, Alias("integration.registration.released")]
public sealed record ReleasedIntegration
{
    [Id(0)] public Dictionary<string, string> Values { get; init; } = [];
    [Id(1)] public IntegrationSnapshot Snapshot { get; init; } = new();

    private bool PrintMembers(StringBuilder builder) => builder.Append("Values = <redacted>") is not null;
}

