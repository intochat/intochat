using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Roslyn;

public sealed class RoslynModuleOptions : IModuleOptions
{
    public string? SolutionPath { get; set; }
    public string WorkspaceKey { get; set; } = "digitalbrain";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceKey))
        { throw new InvalidOperationException("Roslyn workspace key must not be empty."); }
    }
}
