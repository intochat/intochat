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

    [Fact]
    public void EveryCategorizedScreenIsInComponentManifests()
    {
        foreach (var assembly in ProductionArchitecture.Assemblies)
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }

        var categorizedScreens = ProductionArchitecture.Types
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
            .Select(static type => (Type: type, Attribute: type.GetCustomAttribute<ScreenAttribute>()))
            .Where(static pair => pair.Attribute is { Category.Length: > 0 } && !string.IsNullOrWhiteSpace(pair.Attribute.Category))
            .OrderBy(static pair => pair.Type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(categorizedScreens);

        var allById = ComponentManifests.All.ToDictionary(static m => m.Id);
        var problems = new List<string>();
        foreach (var (type, attr) in categorizedScreens)
        {
            var manifest = ComponentManifests.Of(type);
            if (manifest is null || !allById.TryGetValue(manifest.Id, out var registered))
            {
                problems.Add($"{type.FullName}: not registered in ComponentManifests.All");
                continue;
            }
            if (registered.Face is null)
            {
                problems.Add($"{type.FullName} ({registered.Id.Value}): missing Face metadata");
                continue;
            }
            if (string.IsNullOrWhiteSpace(registered.Face.Title)
                || (!string.IsNullOrWhiteSpace(attr!.Title) && !string.Equals(registered.Face.Title, attr.Title, StringComparison.Ordinal)))
            {
                problems.Add($"{type.FullName} ({registered.Id.Value}): expected Face.Title '{attr!.Title}', got '{registered.Face.Title}'");
            }
            if (!string.Equals(registered.Face.Category, attr!.Category, StringComparison.Ordinal))
            {
                problems.Add($"{type.FullName} ({registered.Id.Value}): expected Face.Category '{attr.Category}', got '{registered.Face.Category}'");
            }
        }

        Assert.True(
            problems.Count == 0,
            "Categorized [Screen] classes with missing or mismatched ComponentManifests.All Face metadata:\n"
            + string.Join('\n', problems));
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
