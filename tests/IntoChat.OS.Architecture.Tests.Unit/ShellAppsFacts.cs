using System.Reflection;
using DigitalBrain;
using IntoChat.OS.Apps.Settings;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ShellAppsFacts
{
    [Fact]
    public void TheAppsModuleHasNoShellAppsFolder()
    {
        var shellAppsDir = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Modules.Catalog").Directory, "ShellApps");

        Assert.False(
            Directory.Exists(shellAppsDir),
            $"The Apps module must not contain a ShellApps directory: {shellAppsDir}");
    }

    [Fact]
    public void EveryCompiledAppInIntoChatOsAppsHasAParameterlessConstructor()
    {
        var assembly = typeof(SettingsApp).Assembly;
        var appTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IScreen).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(appTypes);

        foreach (var appType in appTypes)
        {
            var ctors = appType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.Contains(ctors, c => c.GetParameters().Length == 0);
            Assert.All(ctors, c => Assert.True(
                c.GetParameters().All(p => p.HasDefaultValue),
                $"{appType.FullName} constructors must remain zero-arg callable with optional DI parameters only."));
        }
    }
}
