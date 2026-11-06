namespace IntoChat.OS.Modules.Microsoft.Roslyn.Contracts;

public sealed record ScaffoldModuleRequest(
    string Category,
    string Name,
    IReadOnlyList<string> Secrets,
    IReadOnlyList<string> Actions);

public sealed record ScaffoldModuleResult(
    string ModuleDirectory,
    IReadOnlyList<string> CreatedFiles);

[DigitalBrain.Contracts.NeuronAlias("roslyn.module-scaffolder"), DigitalBrain.Contracts.DefaultNeuronType("roslyn.module-scaffolder")]
public interface IModuleScaffolder
{
    Task<ScaffoldModuleResult> Scaffold(ScaffoldModuleRequest request, CancellationToken ct = default);
}
