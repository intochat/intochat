using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Modules;

namespace IntoChat.OS.Architecture.Tests.Unit;

// TEST-01: a module that hosts [Neuron] classes proves them on DigitalBrain.Testing (TestOS)
// in <Module>.Tests.Unit and references no Orleans package.
// A module whose ModuleManifest provides a capability also runs that capability's kit (a *Conformance<T> subclass) in one of its test projects.
public sealed partial class ModuleTestTierFacts
{
    [GeneratedRegex(@"\[\s*(?:DigitalBrain\.)?Neuron\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex NeuronClass();

    [GeneratedRegex(@":\s*(?:[\w.]+\.)?\w+Conformance<", RegexOptions.CultureInvariant)]
    private static partial Regex KitSubclass();

    // Module main projects (ModuleUnitTierFacts' set) whose sources declare a [Neuron] class.
    internal static string[] NeuronModules()
        => [.. Repository.Projects.Values
            .Where(project => project.Name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                && !project.Name.Contains(".Tests", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Contracts", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Testing", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Aspire.Hosting", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Sandbox", StringComparison.Ordinal))
            .Where(project => Repository.SourceFiles(project.Directory, "*.cs").Any(path => NeuronClass().IsMatch(File.ReadAllText(path))))
            .Select(project => project.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void EveryNeuronModuleHasAUnitTierOnTestOS()
    {
        var offenders = new List<string>();
        foreach (var module in NeuronModules())
        {
            if (!Repository.Projects.TryGetValue(module + ".Tests.Unit", out var unit)) { offenders.Add($"{module}: no {module}.Tests.Unit"); continue; }
            var closure = Closure(unit.Path);
            if (!closure.Contains("DigitalBrain.Testing") && !closure.Contains("DigitalBrain.Testing.Silo")) { offenders.Add($"{unit.Name}: does not reach DigitalBrain.Testing (TestOS)"); }
            var orleans = Repository.PackageReferences(unit.Path).Where(package => package.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)).ToArray();
            if (orleans.Length > 0) { offenders.Add($"{unit.Name}: references {string.Join(", ", orleans)}"); }
        }
        Assert.True(offenders.Count == 0, "Neuron modules need a unit tier on TestOS without Orleans (TEST-01):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void AModuleThatProvidesACapabilityRunsItsKit()
    {
        var offenders = new List<string>();
        foreach (var (module, provides) in Providers())
        {
            var kit = Repository.TestProjects().Where(project => project.Name.StartsWith(module + ".Tests.", StringComparison.Ordinal))
                .SelectMany(project => Repository.SourceFiles(project.Directory, "*.cs"))
                .Any(path => KitSubclass().IsMatch(File.ReadAllText(path)));
            // Drive's storage@1 is the remote Drive neuron. A conformance kit needs a Google account, so it is not an in-process subclass.
            if (!kit && module != "IntoChat.OS.Modules.Google.Drive") { offenders.Add($"{module} provides {provides} but no test project of it subclasses a capability kit"); }
        }
        Assert.True(offenders.Count == 0, "Capability providers run the conformance kit (TEST-01, CAP-04):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheScanSeesNeuronModulesAndProviders()
    {
        Assert.Contains("IntoChat.OS.Modules.Identity", NeuronModules());
        Assert.Contains(Providers(), provider => provider.Module == "IntoChat.OS.Modules.Identity");
    }

    // Module assemblies in this repository whose static ModuleManifest provides at least one capability.
    private static (string Module, string Provides)[] Providers()
        => [.. ProductionArchitecture.Types
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.Assembly.GetName().Name!.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                && Repository.Projects.ContainsKey(type.Assembly.GetName().Name!))
            .Select(type => (type, manifest: type.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .Where(pair => pair.manifest?.PropertyType.FullName == "DigitalBrain.Modules.ModuleManifest")
            .Select(pair => (Module: pair.type.Assembly.GetName().Name!, Provides: Provided(pair.manifest!.GetValue(null))))
            .Where(pair => pair.Provides.Length > 0)
            .DistinctBy(pair => pair.Module)];

    private static string Provided(object? manifest)
        => manifest?.GetType().GetProperty("Provides")?.GetValue(manifest) is IEnumerable provides
            ? string.Join(", ", provides.Cast<object>().Select(capability => capability.ToString())) : "";

    // Every project or package a project reaches through references, by name.
    private static HashSet<string> Closure(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.GetFullPath(project)]);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current) || !File.Exists(current)) { continue; }
            names.UnionWith(Repository.PackageReferences(current));
            foreach (var include in XDocument.Load(current).Descendants("ProjectReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>()
                .Where(include => !include.Contains('@', StringComparison.Ordinal)))
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include.Replace('\\', Path.DirectorySeparatorChar)));
                names.Add(Path.GetFileNameWithoutExtension(path));
                pending.Push(path);
            }
        }
        return names;
    }
}
