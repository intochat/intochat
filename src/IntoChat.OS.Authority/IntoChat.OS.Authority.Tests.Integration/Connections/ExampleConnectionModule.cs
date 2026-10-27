using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Orleans.Hosting;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

// A connector whose probe is scripted by the fact: the value "expired" probes Expired, "broken" Failing,
// "throws" throws an exception whose message carries a token-bearing URL.
public sealed class ExampleConnectionModule() : Module(new ModuleId("example"))
{
    public static ConnectorDefinition Connector => new("example", "Example", ["api-key", "oauth"], ["example-oauth"])
    {
        Neurons = [typeof(IExample)],
        OAuthMethods = [new("oauth", "https://provider.test/authorize", "https://provider.test/token", ["read"], "example-oauth")],
    };
    // ClientSecret is optional, as GitHub's is, so a Ready integration can still lack an OAuth client.
    public static IntegrationSchema Integration => IntegrationSchema.For("example-oauth", "Example OAuth").RequiresSecret("ClientId").OffersSecret("ClientSecret");
    public override Type? Neurons => typeof(ExampleConnectorNeurons);
}

[DigitalBrain.Contracts.NeuronAlias("example"), DigitalBrain.Contracts.DefaultNeuronType("example")]
public interface IExample
{
    Task<string> Echo();
    Task<string> SeenValue();
}

[GenerateSerializer, Alias("example.state")]
public sealed class ExampleState : ConnectionState
{
    [Id(8)] public int ProbeCount { get; set; }
}

[DigitalBrain.Neuron("example", State = "example")]
internal sealed class ExampleConnectionNeuron()
    : ConnectionNeuron<ExampleState>, IExample
{
    public const string LeakedToken = "tok-4f1c9e-secret";
    private string? _seen;

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        Snapshot.ProbeCount++;
        _seen = credential.Value;
        if (credential.Value == "throws")
        { throw new HttpRequestException($"GET https://provider.test/me?access_token={LeakedToken} returned 401"); }
        return Task.FromResult(credential.Value switch
        {
            "expired" => ConnectionProbeResult.Expired("expired by script"),
            "broken" => ConnectionProbeResult.Failing("broken by script"),
            _ => ConnectionProbeResult.Connected(),
        });
    }

    public Task<string> Echo() => UseCredentialAsync((credential, _) =>
    {
        _seen = credential.Value;
        return Task.FromResult("len:" + credential.Value.Length);
    }, CancellationToken.None);

    public Task<string> SeenValue() => Task.FromResult(_seen ?? "");
}

// A connector declared and offered with a paste and an OAuth method, but served by no connection
// neuron (as the AI providers' api-key connectors are): no route may address a grain for it.
public sealed class UnservedConnectorModule() : Module(new ModuleId("unserved"))
{
    public static ConnectorDefinition Connector => new("unserved", "Unserved", ["api-key", "oauth"], ["sample-oauth"])
    {
        OAuthMethods = [new("oauth", "https://unserved.test/authorize", "https://unserved.test/token", ["read"], "sample-oauth")],
    };
}

// A second connector beside "example", so a silo holds two IConnection implementations and every
// connection grain must resolve by the connector in its key, not by the contract.
public sealed class SampleConnectionModule() : Module(new ModuleId("sample"))
{
    public static ConnectorDefinition Connector => new("sample", "Sample", ["api-key", "oauth"], ["sample-oauth"])
    {
        Neurons = [typeof(ISample)],
        OAuthMethods = [new("oauth", "https://sample.test/authorize", "https://sample.test/token", ["read"], "sample-oauth")],
    };
    public static IntegrationSchema Integration => IntegrationSchema.For("sample-oauth", "Sample OAuth").RequiresSecret("ClientId").RequiresSecret("ClientSecret");
    public override Type? Neurons => typeof(ExampleConnectorNeurons);
}

[DigitalBrain.Contracts.NeuronAlias("sample"), DigitalBrain.Contracts.DefaultNeuronType("sample")]
public interface ISample
{
    Task<string> Kind();
}

[GenerateSerializer, Alias("sample.state")]
public sealed class SampleState : ConnectionState { }

[DigitalBrain.Neuron("sample", State = "sample")]
internal sealed class SampleConnectionNeuron()
    : ConnectionNeuron<SampleState>, ISample
{
    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
        => Task.FromResult(ConnectionProbeResult.Connected("sample"));

    public Task<string> Kind() => Task.FromResult("sample");
}

// The fact's connection neurons are plain (06-08): this module's assembly [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module("example-connectors")]
public sealed class ExampleConnectorNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
