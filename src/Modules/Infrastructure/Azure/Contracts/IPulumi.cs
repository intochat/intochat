using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Microsoft.Azure;

[GenerateSerializer, Alias("pulumi.stack-summary")]
public sealed record PulumiStackSummary(
    [property: Id(0)] string Env,
    [property: Id(1)] string ResourceGroup,
    [property: Id(2)] string Location,
    [property: Id(3)] string VaultName,
    [property: Id(4)] string OsAppName,
    [property: Id(5)] string? CloudAppName,
    [property: Id(6)] string? SandboxAppName,
    [property: Id(7)] int ResourceCount,
    [property: Id(8)] int SecretBindingCount,
    [property: Id(9)] int VariableCount,
    [property: Id(10)] string PulumiYaml,
    [property: Id(11)] string StackConfigYaml,
    [property: Id(12)] string? LastAction = null,
    [property: Id(13)] string? LastOutput = null,
    [property: Id(14)] DateTimeOffset? UpdatedAt = null);

[DigitalBrain.Signal("pulumi.stack.changed")]
public sealed record PulumiStackChanged(
    string Env,
    string ResourceGroup,
    string Action,
    int ResourceCount) : DigitalBrain.Signal;

/// <summary>
/// Pluggable engine wired by the host to synthesize and execute Pulumi Infrastructure-as-Code stacks.
/// </summary>
public interface IPulumiEngine
{
    Task<PulumiStackSummary> SynthesizeAsync(string env, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default);

    Task<PulumiStackSummary> RunAsync(string env, string verb, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default);
}

[NeuronAlias("pulumi"), DefaultNeuronType("pulumi")]
[Description("Declarative Pulumi Infrastructure-as-Code stack synthesizer and deployment neuron for IntoChat OS, Cloud, and Sandbox environments.")]
public interface IPulumi
{
    [ReadOnlyCall, Interleaves]
    [Description("Read the last synthesized or executed Pulumi stack summary for the environment.")]
    Task<PulumiStackSummary?> Read(string env = "test");

    [Description("Synthesize the Pulumi.yaml and Pulumi.<env>.yaml stack for the environment (optionally including Cloud and Sandbox Container Apps) without contacting Azure.")]
    Task<PulumiStackSummary> Synthesize(string env = "test", bool includeCloud = false, bool includeSandbox = false);

    [CallTimeout(600_000)]
    [Description("Synthesize the Pulumi stack and run 'pulumi preview' for the environment.")]
    Task<PulumiStackSummary> Preview(string env = "test", bool includeCloud = false, bool includeSandbox = false);

    [CallTimeout(900_000)]
    [Description("Synthesize the Pulumi stack and run 'pulumi up --yes' to provision or update the environment on Azure.")]
    Task<PulumiStackSummary> Up(string env = "test", bool includeCloud = false, bool includeSandbox = false);

    static string Key(string brainId) => $"{brainId}/{Relative}";
    const string Relative = "azure/pulumi";
}
