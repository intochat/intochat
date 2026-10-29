using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// The compiled shell apps publish the same script sources the folders did. They have to compile.
public sealed class ShellScriptCompileFacts
{
    [Fact]
    public void WorkspaceDesktopAndSettingsCompile()
    {
        var check = new CSharpScriptCheck();
        foreach (var bundle in new[] { WorkspaceApp.Bundle, DesktopApp.Bundle, SettingsApp.Bundle })
        {
            var scripts = bundle.Files
                .Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
                .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
            var shape = ReactionShape.Check(scripts);
            Assert.Empty(shape);
            var compiled = check.Compile(scripts);
            Assert.True(compiled.Success, bundle.Name + ": " + string.Join("; ", compiled.Errors.Select(error => $"{error.File}:{error.Line} {error.Id} {error.Message}")));
        }
    }
}
