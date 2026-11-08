using System.Reflection;
using System.Text.RegularExpressions;
using DigitalBrain;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Flow<T> replaced seven long-running-work mechanisms. The deleted types stay deleted; the read-only legacy copy lives under
// IntoChat.OS.Modules.Apps.Flows.Legacy. Synapse units react through [Synapse], never [On].
// Scope: production synapse units are compiled at runtime from ISynapseStudio sources, which the digitalbrain analyzers check at
// deploy; the repository holds no compiled production synapse assembly. NoOnInSynapseAssemblies therefore scans the production
// assemblies and every .cs source under the solution projects and features/ (the Apps integration fixture is the synapse
// source that proves the scan sees one). The architecture project's own sources are excluded because they quote the names.
public sealed partial class NoLegacyWorkMechanismsFacts
{
    private static readonly string[] Deleted =
    [
        "ITaskEngine", "TaskNeuron", "TaskExecutorNeuron", "TaskDirectoryNeuron", "IRunNeuron", "RunNeuron",
        "RunGraphSource", "TaskGraphSource", "AppWorkQueue", "IProgress", "ProgressNeuron", "ProgressType",
    ];

    // The BCL System.IProgress<T> is generic and stays; only the non-generic module IProgress is deleted.
    private static readonly Regex DeletedName = new(@"\b(?:" + string.Join('|', Deleted) + @")\b(?!<)", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\[\s*(?:global::[\w.]+\.)?On\s*[\](]", RegexOptions.CultureInvariant)]
    private static partial Regex OnAttributeSource();

    [GeneratedRegex(@"\[\s*(?:global::[\w.]+\.)?Synapse\b", RegexOptions.CultureInvariant)]
    private static partial Regex SynapseAttributeSource();

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsLegacy(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("Legacy", StringComparer.Ordinal);

    private static bool IsLegacyNamespace(string? ns)
        => ns is not null && (ns.EndsWith(".Flows.Legacy", StringComparison.Ordinal)
            || ns.Contains(".Flows.Legacy.", StringComparison.Ordinal));

    private static string StripArity(string name)
        => name.IndexOf('`', StringComparison.Ordinal) is var tick and >= 0 ? name[..tick] : name;

    private static IEnumerable<(string Key, string Path)> Sources(bool includeTests)
    {
        static bool Wanted(string path) => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
        var projects = Repository.ProjectFiles("*.cs", Wanted,
                project => !project.Name.StartsWith("IntoChat.OS.Architecture", StringComparison.Ordinal)
                    && (includeTests || !project.Name.Contains(".Tests", StringComparison.Ordinal)))
            .Select(path => (Repository.ProjectKey(path), path));
        var features = Repository.SourceFiles(Repository.FeatureRoot, "*.cs", Wanted)
            .Select(path => (Repository.Relative(path), path));
        return projects.Concat(features).DistinctBy(file => file.path);
    }

    [Fact]
    public void NoReferencesToDeletedMechanisms()
    {
        var types = ProductionArchitecture.Types
            .Where(type => Deleted.Contains(StripArity(type.Name), StringComparer.Ordinal) && !IsLegacyNamespace(type.Namespace))
            .Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray();
        Assert.True(types.Length == 0, "Deleted work-mechanism types exist again:\n" + string.Join('\n', types));

        var offenders = Sources(includeTests: false)
            .Where(file => !IsLegacy(file.Path) && DeletedName.IsMatch(File.ReadAllText(file.Path)))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Source references a deleted work mechanism; use Flow<T>:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoOnInSynapseAssemblies()
    {
        var methods = ProductionArchitecture.Assemblies.Select(assembly => assembly.GetTypes().SelectMany(type => type.GetMethods(Declared)).ToArray());
        var reflected = methods
            .Where(assembly => assembly.Any(method => method.IsDefined(typeof(SynapseAttribute), false)))
            .SelectMany(assembly => assembly.Where(method => method.IsDefined(typeof(OnAttribute), false)))
            .Select(method => method.DeclaringType?.FullName + "." + method.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.True(reflected.Length == 0, "[On] methods in assemblies that declare synapses:\n" + string.Join('\n', reflected));

        var synapseSources = Sources(includeTests: true)
            .Select(file => (file.Key, Text: File.ReadAllText(file.Path)))
            .Where(file => SynapseAttributeSource().IsMatch(file.Text)).ToArray();
        Assert.True(synapseSources.Length > 0, "No source declaring [Synapse] was scanned; the fact would be vacuous.");
        var offenders = synapseSources.Where(file => OnAttributeSource().IsMatch(file.Text))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Sources declare both [Synapse] and [On]:\n" + string.Join('\n', offenders));
    }
}
