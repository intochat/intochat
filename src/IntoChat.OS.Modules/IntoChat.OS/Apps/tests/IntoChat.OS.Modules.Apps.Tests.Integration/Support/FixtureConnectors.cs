using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps.Tests;

// Two connection kinds a slot can bind, each with a pasted credential that probes connected, except the credential
// "fixture-value-expired", which probes expired. Drive declares the storage capability, as the Windows and Google
// Drive modules do.
public sealed class FixtureConnectorsModule() : Module(new ModuleId("fixture-connectors"))
{
    public static ConnectorDefinition[] Connectors =>
    [
        new("mailbox", "Mailbox", ["api-key"], []) { Neurons = [typeof(IFixtureMailbox)] },
        new("drive", "Drive", ["api-key"], []) { Neurons = [typeof(IFixtureDrive)], Capabilities = ["storage"] },
    ];

    public override void Configure(ISiloBuilder silo) { }
}

[Alias("fixture.mailbox"), Orleans.Metadata.DefaultGrainType("mailbox")]
public interface IFixtureMailbox : INeuron { Task<string> Kind(); }

[Alias("fixture.drive"), Orleans.Metadata.DefaultGrainType("drive")]
public interface IFixtureDrive : IntoChat.OS.Integrations.IStorage { Task<string> Kind(); }

[GenerateSerializer, Alias("fixture.connection.state")]
public sealed class FixtureConnectionState : ConnectionState { }

[GrainType("mailbox")]
internal sealed class FixtureMailboxNeuron(
    [PersistentState("mailbox", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<FixtureConnectionState> store)
    : ConnectionNeuron<FixtureConnectionState>(store), IFixtureMailbox
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(credential.Value == "fixture-value-expired" ? ConnectionProbeResult.Expired("expired by fixture") : ConnectionProbeResult.Connected());
    public Task<string> Kind() => Task.FromResult("mailbox");
}

[GrainType("drive")]
internal sealed class FixtureDriveNeuron(
    [PersistentState("drive", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<FixtureConnectionState> store)
    : ConnectionNeuron<FixtureConnectionState>(store), IFixtureDrive
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(credential.Value == "fixture-value-expired" ? ConnectionProbeResult.Expired("expired by fixture") : ConnectionProbeResult.Connected());
    public Task<string> Kind() => Task.FromResult("drive");

    // An empty storage: install facts need the capability declared, not its content.
    public Task<IntoChat.OS.Integrations.StorageEntry[]> Roots(CancellationToken cancellationToken = default) => Task.FromResult<IntoChat.OS.Integrations.StorageEntry[]>([]);
    public Task<IntoChat.OS.Integrations.StorageEntry[]> List(string location, CancellationToken cancellationToken = default) => Task.FromResult<IntoChat.OS.Integrations.StorageEntry[]>([]);
    public Task<string> Read(string location, CancellationToken cancellationToken = default) => Task.FromResult("");
    public Task<IntoChat.OS.Integrations.StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default) => Task.FromResult<IntoChat.OS.Integrations.StorageEntry[]>([]);
}
