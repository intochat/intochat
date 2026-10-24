using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using Orleans.Hosting;

namespace IntoChat.OS.Apps.Tests;

// Two connector kinds a slot can bind, each with a pasted credential that always probes connected.
public sealed class FixtureConnectorsModule() : Module(new ModuleId("fixture-connectors"))
{
    public static ConnectorDefinition[] Connectors =>
    [
        new("mailbox", "Mailbox", ["api-key"], []) { Neurons = [typeof(IFixtureMailbox)] },
        new("drive", "Drive", ["api-key"], []) { Neurons = [typeof(IFixtureDrive)] },
    ];

    public override void Configure(ISiloBuilder silo) { }
}

[Alias("fixture.mailbox"), Orleans.Metadata.DefaultGrainType("mailbox")]
public interface IFixtureMailbox : INeuron { Task<string> Kind(); }

[Alias("fixture.drive"), Orleans.Metadata.DefaultGrainType("drive")]
public interface IFixtureDrive : INeuron { Task<string> Kind(); }

[GenerateSerializer, Alias("fixture.connection.state")]
public sealed class FixtureConnectionState : ConnectionState { }

[GrainType("mailbox")]
internal sealed class FixtureMailboxNeuron(
    [PersistentState("mailbox", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<FixtureConnectionState> store)
    : ConnectionNeuron<FixtureConnectionState>(store), IFixtureMailbox
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) => Task.FromResult(ConnectionProbeResult.Connected());
    public Task<string> Kind() => Task.FromResult("mailbox");
}

[GrainType("drive")]
internal sealed class FixtureDriveNeuron(
    [PersistentState("drive", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<FixtureConnectionState> store)
    : ConnectionNeuron<FixtureConnectionState>(store), IFixtureDrive
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) => Task.FromResult(ConnectionProbeResult.Connected());
    public Task<string> Kind() => Task.FromResult("drive");
}
