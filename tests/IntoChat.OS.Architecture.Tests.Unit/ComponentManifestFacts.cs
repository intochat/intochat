using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain.Components;
using DigitalBrain.Modules;
using IntoChat.OS.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ComponentManifestFacts
{
    [Fact]
    public void EveryModuleAndScreenInLoadedAssembliesHasARegisteredManifest()
    {
        foreach (var assembly in ProductionArchitecture.Assemblies)
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }

        var moduleTypes = ProductionArchitecture.Types
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && (type.GetCustomAttribute<ModuleAttribute>() is not null || DerivesFromModule(type)))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        var screenTypes = ProductionArchitecture.Types
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && type.GetCustomAttribute<ScreenAttribute>() is not null)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(moduleTypes);
        Assert.NotEmpty(screenTypes);

        var missing = new List<string>();
        foreach (var type in moduleTypes.Concat(screenTypes).Distinct())
        {
            var manifest = ComponentManifests.Of(type);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id.Value))
            {
                missing.Add(type.FullName ?? type.Name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Modules or screens without a registered ComponentManifest:\n" + string.Join('\n', missing));
    }

    private static bool DerivesFromModule(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current == typeof(DigitalBrain.Modules.Module))
            {
                return true;
            }
        }
        return false;
    }
}
