using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using Orleans.Hosting;

namespace DigitalBrain.Platform.Tests.Unit.Connections;

// A connector whose probe is scripted by the fact: the value "expired" probes Expired, "broken" Failing,
// "throws" throws an exception whose message carries a token-bearing URL.
public sealed class ExampleConnectionModule() : Module(new ModuleId("example"))
{
    public static ConnectorDefinition Connector => new("example", "Example", ["api-key", "oauth"], []) { Neurons = [typeof(IExample)] };
    public override void Configure(ISiloBuilder silo) { }
}

[Alias("example"), Orleans.Metadata.DefaultGrainType("example")]
public interface IExample : INeuron
{
    Task<string> Echo();
    Task<string> SeenValue();
}

[GenerateSerializer, Alias("example.state")]
public sealed class ExampleState : ConnectionState
{
    [Id(8)] public int ProbeCount { get; set; }
}

[GrainType("example")]
internal sealed class ExampleConnectionNeuron(
    [PersistentState("example", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ExampleState> store)
    : ConnectionNeuron<ExampleState>(store), IExample
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
