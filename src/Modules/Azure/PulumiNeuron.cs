using System.Diagnostics;
using DigitalBrain.Neurons;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Microsoft.Azure;

[GenerateSerializer, Alias("pulumi.state")]
[ExportableState]
internal sealed record PulumiState
{
    [Id(0)] public PulumiStackSummary[] Stacks { get; init; } = [];
}

[Neuron("pulumi")]
internal sealed class PulumiNeuron(IPulumiEngine? engine = null) : Neuron<PulumiState>, IPulumi
{
    private readonly IPulumiEngine _engine = engine ?? new DefaultPulumiEngine();

    public Task<PulumiStackSummary?> Read(string env = "test")
    {
        var normalized = NormalizeEnv(env);
        var found = State.Stacks.FirstOrDefault(s => string.Equals(s.Env, normalized, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(found);
    }

    public async Task<PulumiStackSummary> Synthesize(string env = "test", bool includeCloud = false, bool includeSandbox = false)
    {
        var normalized = NormalizeEnv(env);
        var summary = await _engine.SynthesizeAsync(normalized, includeCloud, includeSandbox, CancellationToken.None);
        await RecordAsync(summary, "synthesize");
        return summary;
    }

    public async Task<PulumiStackSummary> Preview(string env = "test", bool includeCloud = false, bool includeSandbox = false)
    {
        var normalized = NormalizeEnv(env);
        var summary = await _engine.RunAsync(normalized, "preview", includeCloud, includeSandbox, CancellationToken.None);
        await RecordAsync(summary, "preview");
        return summary;
    }

    public async Task<PulumiStackSummary> Up(string env = "test", bool includeCloud = false, bool includeSandbox = false)
    {
        var normalized = NormalizeEnv(env);
        var summary = await _engine.RunAsync(normalized, "up", includeCloud, includeSandbox, CancellationToken.None);
        await RecordAsync(summary, "up");
        return summary;
    }

    private async Task RecordAsync(PulumiStackSummary summary, string action)
    {
        var kept = State.Stacks.Where(s => !string.Equals(s.Env, summary.Env, StringComparison.OrdinalIgnoreCase)).ToArray();
        var signal = new PulumiStackChanged(summary.Env, summary.ResourceGroup, action, summary.ResourceCount);
        await Save(State with { Stacks = [.. kept, summary] }, signal);
    }

    private static string NormalizeEnv(string? env) =>
        string.IsNullOrWhiteSpace(env) ? "test" : env.Trim().ToLowerInvariant();
}

internal sealed class DefaultPulumiEngine : IPulumiEngine
{
    public Task<PulumiStackSummary> SynthesizeAsync(string env, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default)
    {
        var rg = ResourceNames.ResourceGroup("intochat", env);
        var vault = ResourceNames.KeyVault("intochat", env);
        var osApp = ResourceNames.App("intochat", env, "os");
        var cloudApp = includeCloud ? ResourceNames.App("intochat", env, "cloud") : null;
        var sandboxApp = includeSandbox ? ResourceNames.App("intochat", env, "sandbox") : null;
        var count = 9 + (includeCloud ? 1 : 0) + (includeSandbox ? 1 : 0);
        return Task.FromResult(new PulumiStackSummary(
            Env: env,
            ResourceGroup: rg,
            Location: "polandcentral",
            VaultName: vault,
            OsAppName: osApp,
            CloudAppName: cloudApp,
            SandboxAppName: sandboxApp,
            ResourceCount: count,
            SecretBindingCount: 1,
            VariableCount: 16,
            PulumiYaml: $"name: intochat-os\nruntime: yaml\n",
            StackConfigYaml: $"config:\n  intochat-os:env: \"{env}\"\n",
            LastAction: "synthesize",
            LastOutput: $"Synthesized stack '{env}' ({count} resources).",
            UpdatedAt: DateTimeOffset.UtcNow));
    }

    public async Task<PulumiStackSummary> RunAsync(string env, string verb, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default)
    {
        var summary = await SynthesizeAsync(env, includeCloud, includeSandbox, cancellationToken);
        return summary with
        {
            LastAction = verb,
            LastOutput = $"Completed pulumi {verb} for '{env}'.",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}
