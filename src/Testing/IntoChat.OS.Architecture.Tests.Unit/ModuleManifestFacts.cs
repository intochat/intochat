using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// R4-04: the module is the unit, and its static DigitalBrain ModuleManifest replaces the silo-builder registrations.
// Every module class declares one. There is no missing-manifest baseline.
public sealed class ModuleManifestFacts
{
    private static readonly string[] ModuleAssemblyPrefixes = ["IntoChat.OS.Modules.", "IntoChat.Cloud."];

    internal static string[] Modules(bool withManifest)
        => ProductionArchitecture.Types
            .Where(type => type is { IsClass: true, IsAbstract: false } && IsModule(type))
            .Where(type => ModuleAssemblyPrefixes.Any(prefix => type.Assembly.GetName().Name!.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(type => HasManifest(type) == withManifest)
            .Select(type => type.FullName!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsModule(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == "DigitalBrain.Module") { return true; }
        }
        return false;
    }

    private static bool HasManifest(Type type)
        => type.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.PropertyType.FullName == "DigitalBrain.ModuleManifest";

    [Fact]
    public void EveryModuleDeclaresAManifest()
    {
        var missing = Modules(withManifest: false);
        Assert.True(missing.Length == 0, "Modules without a static DigitalBrain ModuleManifest Manifest property:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void TheScanSeesTheManifestModules() => Assert.Contains("IntoChat.OS.Modules.Google.Drive.DriveModule", Modules(withManifest: true));
}
