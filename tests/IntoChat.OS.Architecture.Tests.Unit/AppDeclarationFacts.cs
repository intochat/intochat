using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.UI;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class AppDeclarationFacts
{
    [Fact]
    public void EveryCompiledAppScreenDeclaresCompleteScreenMetadataAndMatchesComponentManifest()
    {
        foreach (var assembly in ProductionArchitecture.Assemblies)
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }

        var appScreenTypes = ProductionArchitecture.Types
            .Where(static type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && typeof(IScreen).IsAssignableFrom(type))
            .Where(static type =>
            {
                var neuron = type.GetCustomAttribute<NeuronAttribute>();
                return neuron is not null && neuron.Type.StartsWith("app.", StringComparison.Ordinal);
            })
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(appScreenTypes);

        var problems = new List<string>();
        foreach (var type in appScreenTypes)
        {
            var screen = type.GetCustomAttribute<ScreenAttribute>();
            if (screen is null)
            {
                problems.Add($"{type.FullName}: missing [Screen] attribute");
                continue;
            }

            if (string.IsNullOrWhiteSpace(screen.Title))
            {
                problems.Add($"{type.FullName}: [Screen] must declare a non-empty Title");
            }

            var component = ComponentManifests.Of(type);
            if (component?.Face is null)
            {
                problems.Add($"{type.FullName}: missing generated ComponentManifest.Face");
                continue;
            }

            var expectedDock = screen.Dock >= 0 ? screen.Dock : (int?)null;
            var expectedMaximized = screen.Launch == AppLaunch.Maximized;

            if (!string.Equals(component.Face.Title, screen.Title, StringComparison.Ordinal))
            {
                problems.Add($"{type.FullName}: Face.Title '{component.Face.Title}' != [Screen].Title '{screen.Title}'");
            }

            if (!string.Equals(component.Face.Icon, screen.Icon, StringComparison.Ordinal))
            {
                problems.Add($"{type.FullName}: Face.Icon '{component.Face.Icon}' != [Screen].Icon '{screen.Icon}'");
            }

            if (!string.Equals(component.Face.Category, screen.Category, StringComparison.Ordinal))
            {
                problems.Add($"{type.FullName}: Face.Category '{component.Face.Category}' != [Screen].Category '{screen.Category}'");
            }

            if (component.Face.Dock != expectedDock)
            {
                problems.Add($"{type.FullName}: Face.Dock '{component.Face.Dock}' != [Screen].Dock '{expectedDock}'");
            }

            if (component.Face.Maximized != expectedMaximized)
            {
                problems.Add($"{type.FullName}: Face.Maximized '{component.Face.Maximized}' != [Screen].Launch '{screen.Launch}'");
            }

            var bundleProperty = type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static);
            if (bundleProperty?.PropertyType == typeof(AppBundle) && bundleProperty.GetValue(null) is AppBundle bundle)
            {
                var manifest = bundle.Manifest;
                if (!string.Equals(manifest.Title, screen.Title, StringComparison.Ordinal))
                {
                    problems.Add($"{type.FullName}.Bundle: Manifest.Title '{manifest.Title}' != [Screen].Title '{screen.Title}'");
                }

                if (!string.Equals(manifest.Icon, screen.Icon, StringComparison.Ordinal))
                {
                    problems.Add($"{type.FullName}.Bundle: Manifest.Icon '{manifest.Icon}' != [Screen].Icon '{screen.Icon}'");
                }

                if (!string.Equals(manifest.Category, screen.Category, StringComparison.Ordinal))
                {
                    problems.Add($"{type.FullName}.Bundle: Manifest.Category '{manifest.Category}' != [Screen].Category '{screen.Category}'");
                }

                if (manifest.Dock != expectedDock)
                {
                    problems.Add($"{type.FullName}.Bundle: Manifest.Dock '{manifest.Dock}' != [Screen].Dock '{expectedDock}'");
                }

                if (manifest.Launch != screen.Launch)
                {
                    problems.Add($"{type.FullName}.Bundle: Manifest.Launch '{manifest.Launch}' != [Screen].Launch '{screen.Launch}'");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Compiled app [Screen] declaration mismatches:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void CompiledAppBundlesDoNotInstantiateAppManifestDirectly()
    {
        var roots = new[]
        {
            Path.Combine(Repository.Root, "src", "IntoChat.OS.Apps"),
            Path.Combine(Repository.Root, "src", "Components", "Aspire", "Screens"),
            Path.Combine(Repository.Root, "src", "Components", "Muncly", "Screens"),
        };

        var violations = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals("ShippedUnits.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = File.ReadAllText(file);
                if (content.Contains("new AppManifest(", StringComparison.Ordinal))
                {
                    violations.Add(Path.GetRelativePath(Repository.Root, file));
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Compiled app screens must build AppBundle via AppBundle.FromScreen<TApp>(...) so face metadata lives only in [Screen]:\n"
            + string.Join('\n', violations));
    }

    [Fact]
    public void DesktopAppDoesNotHardcodeAppCatalogOrDockOrder()
    {
        var path = Path.Combine(Repository.Root, "src", "IntoChat.OS.Apps", "Desktop", "DesktopApp.cs");
        Assert.True(File.Exists(path), $"Expected DesktopApp at {path}");
        var source = File.ReadAllText(path);

        var forbiddenTokens = new[]
        {
            "DefaultDockApps",
            "NativeCatalog",
            "EffectiveCatalog",
            "ArtCraftCatalog",
            "IntoChat.OS.Modules.ArtCraft",
        };

        var hits = forbiddenTokens
            .Where(token => source.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            hits.Length == 0,
            "DesktopApp must render dock and launcher tiles from IInstalledApps data, not hardcoded catalogs:\n"
            + string.Join('\n', hits));
    }

    [Fact]
    public void FlutterDesktopShellDoesNotHardcodeAppCatalogOrLaunchRules()
    {
        var targets = new List<string>();
        var desktopDir = Path.Combine(Repository.Root, "ui", "lib", "desktop");
        if (Directory.Exists(desktopDir))
        {
            targets.AddRange(Directory.EnumerateFiles(desktopDir, "*.dart", SearchOption.AllDirectories));
        }

        var knownApps = Path.Combine(Repository.Root, "ui", "lib", "workspace", "known_apps.dart");
        if (File.Exists(knownApps))
        {
            targets.Add(knownApps);
        }

        Assert.NotEmpty(targets);

        var forbiddenTokens = new[]
        {
            "_defaultSmartAppIds",
            "_artCraftAppIds",
            "_coreSmartAppIds",
            "defaultSmartAppSpecs",
            "artCraftSmartAppSpecs",
            "allSmartAppSpecs",
            "_defaultCategories",
            "normalized == 'orleans'",
            "normalized == 'aspire'",
            "normalized == 'vectorcraft'",
        };

        var violations = new List<string>();
        foreach (var file in targets.OrderBy(static f => f, StringComparer.Ordinal))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbiddenTokens)
            {
                if (source.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(Repository.Root, file)}: forbidden token '{token}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Flutter desktop shell must render launcher, dock, and launch rules from server AppTileNode data, not hardcoded app lists:\n"
            + string.Join('\n', violations));
    }
}
