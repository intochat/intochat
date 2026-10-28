using System.Reflection;
using System.Text.Json;

namespace IntoChat.OS.Architecture.Tests.Unit;

// R4-04: the module is the unit, and its static DigitalBrain ModuleManifest (Provides, Requires, Needs, Settings,
// Secrets) replaces the silo-builder registrations. Each family PR adds the manifests of the modules it moves
// and shrinks ModulesMissingManifest.json; the list ends empty.
public sealed class ModuleManifestFacts
{
    internal const string FileName = "ModulesMissingManifest.json";

    private static readonly string[] ModuleAssemblyPrefixes = ["IntoChat.OS.Modules.", "IntoChat.OS.Connections", "IntoChat.OS.Mcp"];

    internal static string[] Modules(bool withManifest)
        => CaptureWriter.Ordered(ProductionArchitecture.Types
            .Where(type => type is { IsClass: true, IsAbstract: false } && IsModule(type))
            .Where(type => ModuleAssemblyPrefixes.Any(prefix => type.Assembly.GetName().Name!.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(type => HasManifest(type) == withManifest)
            .Select(type => type.FullName!));

    private static bool IsModule(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName is "IntoChat.OS.Framework.Modules.Module" or "DigitalBrain.Module") { return true; }
        }
        return false;
    }

    private static bool HasManifest(Type type)
        => type.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.PropertyType.FullName == "DigitalBrain.ModuleManifest";

    [Fact]
    public void ModuleManifestPresent()
    {
        var missing = JsonSerializer.Deserialize<string[]>(File.ReadAllText(CaptureWriter.Path(FileName)))!.ToHashSet(StringComparer.Ordinal);
        var current = Modules(withManifest: false);

        var added = current.Where(name => !missing.Contains(name)).ToArray();
        Assert.True(added.Length == 0,
            "Modules without a static DigitalBrain ModuleManifest Manifest property; declare one (R4-04):\n" + string.Join('\n', added));

        var gained = missing.Where(name => !current.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(gained.Length == 0, $"These modules have a manifest now (or are gone); shrink the file {FileName}:\n" + string.Join('\n', gained));
    }

    [Fact]
    public void TheScanSeesTheManifestModules() => Assert.Contains("IntoChat.OS.Modules.Google.Drive.DriveModule", Modules(withManifest: true));

    [Fact(Explicit = true)]
    public void CaptureModulesMissingManifest() => CaptureWriter.Write(FileName, Modules(withManifest: false));
}
