using System.Reflection;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// The compiled shell apps declare an [App] class and compile cleanly against the sandbox contracts.
public sealed class ShellScriptCompileFacts
{
    [Fact]
    public void WorkspaceDesktopAndSettingsCompile()
    {
        var check = new CSharpScriptCheck();
        foreach (var appType in new[] { typeof(WorkspaceApp), typeof(DesktopApp), typeof(SettingsApp) })
        {
            var appAttr = appType.GetCustomAttribute<DigitalBrain.AppAttribute>();
            Assert.NotNull(appAttr);
            var source = $$"""
                [DigitalBrain.App("{{appAttr!.Id}}")]
                public sealed class Dynamic{{appType.Name}} : DigitalBrain.App
                {
                    public static void Run() { }
                }
                """;
            var compiled = check.Compile(new Dictionary<string, string> { [$"{appType.Name}.cs"] = source });
            Assert.True(compiled.Success, appType.Name + ": " + string.Join("; ", compiled.Errors.Select(error => $"{error.File}:{error.Line} {error.Id} {error.Message}")));
        }
    }
}
