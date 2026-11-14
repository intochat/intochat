using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

public sealed class RoslynModuleOptions : IModuleOptions
{
    public string? SolutionPath { get; set; }
    public string WorkspaceKey { get; set; } = "intochat-os";
    public string[] AllowedRoots { get; set; } = [];
    public IDictionary<string, string> Repositories { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceKey))
        { throw new InvalidOperationException("Roslyn workspace key must not be empty."); }
    }
}
