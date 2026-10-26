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

    public override Type? Neurons => typeof(FixtureConnectorNeurons);
}

[DigitalBrain.Contracts.NeuronAlias("fixture.mailbox"), DigitalBrain.Contracts.DefaultNeuronType("mailbox")]
public interface IFixtureMailbox { Task<string> Kind(); }

[DigitalBrain.Contracts.NeuronAlias("fixture.drive"), DigitalBrain.Contracts.DefaultNeuronType("drive")]
public interface IFixtureDrive : IntoChat.OS.Integrations.IStorage { Task<string> Kind(); }

[GenerateSerializer, Alias("fixture.connection.state")]
public sealed class FixtureConnectionState : ConnectionState { }

[DigitalBrain.Neuron("mailbox", State = "mailbox")]
internal sealed class FixtureMailboxNeuron()
    : ConnectionNeuron<FixtureConnectionState>, IFixtureMailbox
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(credential.Value == "fixture-value-expired" ? ConnectionProbeResult.Expired("expired by fixture") : ConnectionProbeResult.Connected());
    public Task<string> Kind() => Task.FromResult("mailbox");
}

[DigitalBrain.Neuron("drive", State = "drive")]
internal sealed class FixtureDriveNeuron()
    : ConnectionNeuron<FixtureConnectionState>, IFixtureDrive
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

// The fact's connection neurons are plain (06-08): this module's assembly [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module("fixture-connectors")]
public sealed class FixtureConnectorNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
