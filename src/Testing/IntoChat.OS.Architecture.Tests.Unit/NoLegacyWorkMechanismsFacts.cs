using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Flow<T> replaced seven long-running-work mechanisms. The deleted types stay deleted; the read-only legacy copy lives under
// IntoChat.OS.Modules.Apps.Flows.Legacy. Synapse units are compiled from source and react through [Synapse], never [On].
public sealed partial class NoLegacyWorkMechanismsFacts
{
    private static readonly string[] Deleted =
    [
        "ITaskEngine", "TaskNeuron", "TaskExecutorNeuron", "TaskDirectoryNeuron", "IRunNeuron", "RunNeuron",
        "RunGraphSource", "TaskGraphSource", "AppWorkQueue", "IProgress", "ProgressNeuron",
    ];

    // The BCL System.IProgress<T> is generic and stays; only the non-generic module IProgress is deleted.

    private static readonly Regex DeletedName = new(@"\b(?:" + string.Join('|', Deleted) + @")\b(?!<)", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\[\s*On\s*[\](]", RegexOptions.CultureInvariant)]
    private static partial Regex OnAttribute();

    private static bool IsLegacy(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("Legacy", StringComparer.Ordinal);

    private static bool IsLegacyNamespace(string? ns)
        => ns is not null && (ns.EndsWith(".Flows.Legacy", StringComparison.Ordinal)
            || ns.Contains(".Flows.Legacy.", StringComparison.Ordinal));

    [Fact]
    public void NoReferencesToDeletedMechanisms()
    {
        var types = ProductionArchitecture.Types
            .Where(type => Deleted.Contains(type.Name, StringComparer.Ordinal) && !IsLegacyNamespace(type.Namespace))
            .Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray();
        Assert.True(types.Length == 0, "Deleted work-mechanism types exist again:\n" + string.Join('\n', types));

        var offenders = Repository.ProjectFiles("*.cs",
                path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !IsLegacy(path),
                project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && !project.Name.StartsWith("IntoChat.OS.Architecture", StringComparison.Ordinal))
            .Where(path => DeletedName.IsMatch(File.ReadAllText(path)))
            .Select(Repository.ProjectKey).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Source references a deleted work mechanism; use Flow<T>:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoOnInSynapseAssemblies()
    {
        var assemblies = ProductionArchitecture.Assemblies
            .Where(assembly => assembly.GetTypes().Any(type => type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Any(method => method.GetCustomAttributes(false).Any(a => a.GetType().Name == "SynapseAttribute"))))
            .ToArray();
        var reflected = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes(false).Any(a => a.GetType().Name == "OnAttribute"))
            .Select(method => method.DeclaringType?.FullName + "." + method.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.True(reflected.Length == 0, "[On] methods in assemblies that declare synapses:\n" + string.Join('\n', reflected));

        // Synapse units are compiled from source, so also scan every source that declares a [Synapse(...)].
        var sources = Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Key: Repository.ProjectKey(path), Text: File.ReadAllText(path)))
            .Where(file => !file.Key.Contains(".Tests", StringComparison.Ordinal) || !file.Key.StartsWith("IntoChat.OS.Architecture", StringComparison.Ordinal))
            .Where(file => !file.Key.StartsWith("IntoChat.OS.Architecture.Tests", StringComparison.Ordinal))
            .Where(file => file.Text.Contains("[Synapse(", StringComparison.Ordinal) && OnAttribute().IsMatch(file.Text))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(sources.Length == 0, "Sources declare both [Synapse] and [On]:\n" + string.Join('\n', sources));
    }
}
