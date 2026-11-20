using System.Reflection;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.UI;

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
            Assert.NotEmpty(ctors);
            Assert.Contains(ctors, c => c.GetParameters().Length == 0);
            Assert.All(ctors, c => Assert.True(
                c.GetParameters().All(p => p.HasDefaultValue),
                $"{appType.FullName} constructors must remain zero-arg callable with optional DI parameters only."));
        }
    }

    [Fact]
    public void EveryCompiledAppInIntoChatOsAppsIsAnAggregateScreen()
    {
        var assembly = typeof(SettingsApp).Assembly;
        var appTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && typeof(IScreen).IsAssignableFrom(t)
                && !IsControlNeuron(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(appTypes.Length >= 30, $"Expected at least 30 compiled shell apps, found {appTypes.Length}.");
        Assert.Contains(typeof(DesktopApp), appTypes);
        Assert.Contains(typeof(InoApp), appTypes);

        var nonAggregateApps = appTypes
            .Where(t => !DerivesFromGeneric(t, typeof(AggregateScreen<>)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            nonAggregateApps.Length == 0,
            "Every compiled shell app in IntoChat.OS.Apps must derive from AggregateScreen<TState>:\n"
            + string.Join('\n', nonAggregateApps));
    }

    private static bool IsControlNeuron(Type type)
        => type.GetInterfaces().Any(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IControl<>));

    private static bool DerivesFromGeneric(Type type, Type openGenericBase)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericBase)
            {
                return true;
            }
        }

        return false;
    }
}
