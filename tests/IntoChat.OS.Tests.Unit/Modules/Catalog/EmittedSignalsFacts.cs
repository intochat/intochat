using System.Reflection;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// A compiled app's view sends typed commands: a button's Sends(new T(...)) reaches the [On] reaction of the screen that drew it.
// This scans the source of the compiled apps (the IntoChat.OS.Apps project) and of the Apps module's host-built views for every
// typed command and checks that a screen of those assemblies reacts to its signal, so no button sends what nothing handles.
// The host's own emit helpers (BrainEmit, DesktopEmit, DesktopButton, DesktopIcon) still name operations; those are checked
// against the emitting app's Bundle.Manifest.Operations.
public sealed partial class EmittedSignalsFacts
{
    [Fact]
    public void EveryTypedCommandOfACompiledAppIsHandledByAScreen()
    {
        var root = RepositoryRoot();
        var signals = SignalTypes();
        var handled = ScreenSignalAliases();
        var unhandled = new List<string>();
        var found = 0;

        foreach (var (file, source) in Sources(root))
        {
            foreach (Match match in TypedCommand().Matches(source))
            {
                found++;
                var site = $"{Path.GetRelativePath(root, file)}:{source[..match.Index].Count(c => c == '\n') + 1}: {match.Value.Trim()}";
                var name = match.Groups["type"].Value;
                if (!signals.TryGetValue(name, out var alias)) { unhandled.Add($"{site}: no signal type '{name}'"); }
                else if (!handled.Contains(alias)) { unhandled.Add($"{site}: no screen reacts to '{alias}'"); }
            }
        }

        Assert.True(found > 40, $"Only {found} typed commands found; the scan pattern no longer matches the source.");
        Assert.True(unhandled.Count == 0, string.Join(Environment.NewLine, unhandled));
    }

    [Fact]
    public void EveryHostEmittedOperationIsDeclaredInItsManifest()
    {
        var root = RepositoryRoot();
        var operations = Manifests();
        var undeclared = new List<string>();
        foreach (var (file, source) in Sources(root))
        {
            foreach (Match match in HostEmit().Matches(source))
            {
                var owner = match.Groups["helper"].Value.StartsWith("Brain", StringComparison.Ordinal) ? "brain" : "desktop";
                var signal = match.Groups["signal"].Value;
                if (!signal.StartsWith('"')) { continue; }
                var site = $"{Path.GetRelativePath(root, file)}:{source[..match.Index].Count(c => c == '\n') + 1}: {match.Value.Trim()}";
                if (!operations.TryGetValue(owner, out var declared)) { undeclared.Add($"{site}: no manifest for app '{owner}'"); }
                else if (!declared.Contains(signal.Trim('"'))) { undeclared.Add($"{site}: '{owner}' does not declare '{signal}'"); }
            }
        }

        Assert.True(undeclared.Count == 0, string.Join(Environment.NewLine, undeclared));
    }

    private static IEnumerable<(string File, string Source)> Sources(string root)
        => Directory.EnumerateFiles(ProjectDirectory(root, "IntoChat.OS.Apps"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ProjectDirectory(root, "IntoChat.OS.Modules.Catalog"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // Shipped unit sources (Units/) are synapse units: their view commands their own state alias.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Units{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, File.ReadAllText(path)));

    // Every signal record the scanned assemblies (and those they reference) define, by simple name, with its alias.
    private static Dictionary<string, string> SignalTypes()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in Assemblies().SelectMany(Types).Where(type => typeof(DigitalBrain.Signal).IsAssignableFrom(type) && !type.IsAbstract))
        {
            if (type.GetCustomAttribute<DigitalBrain.SignalAttribute>(false) is { } signal) { result.TryAdd(type.Name, signal.Alias); }
        }
        return result;
    }

    // The aliases some screen of the compiled apps or the Apps module reacts to with [On].
    private static HashSet<string> ScreenSignalAliases()
        => [.. new[] { typeof(IntoChat.OS.Apps.AppSignals).Assembly, typeof(BrainGraph).Assembly }
            .SelectMany(Types)
            .Where(type => !type.IsAbstract && type.GetInterfaces().Contains(typeof(DigitalBrain.IScreen)))
            .SelectMany(type => DigitalBrain.Kernel.ScreenSignals.For(type).Keys)];

    private static IEnumerable<Assembly> Assemblies()
    {
        var roots = new[] { typeof(IntoChat.OS.Apps.AppSignals).Assembly, typeof(BrainGraph).Assembly };
        return roots.Concat(roots.SelectMany(assembly => assembly.GetReferencedAssemblies()).Select(name =>
        {
            try { return Assembly.Load(name); }
            catch (FileNotFoundException) { return null; }
        }).OfType<Assembly>()).Distinct();
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException partial) { return partial.Types.OfType<Type>(); }
    }

    private static Dictionary<string, HashSet<string>> Manifests()
    {
        var bundles = typeof(IntoChat.OS.Apps.AppSignals).Assembly.GetTypes()
            .Select(type => type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null))
            .OfType<AppBundle>();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var bundle in bundles)
        {
            if (!result.TryGetValue(bundle.Name, out var names)) { result[bundle.Name] = names = new(StringComparer.Ordinal); }
            names.UnionWith(bundle.Manifest.Operations.Select(operation => operation.Name));
        }
        return result;
    }

    // A project's folder, looked up by name in the solution.
    private static string ProjectDirectory(string root, string project)
    {
        var solution = File.ReadAllText(Path.Combine(root, "Intochat.OS.slnx"));
        var path = Regex.Matches(solution, "Path=\"([^\"]+)\"").Select(match => match.Groups[1].Value)
            .Single(candidate => Path.GetFileName(candidate) == project + ".csproj");
        return Path.GetDirectoryName(Path.Combine(root, path))!;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("The repository root (Intochat.OS.slnx) was not found.");
    }
    // Sends(new T(...)): the typed signal a button, field or nav item sends (a raw Command is not a typed signal).
    // Sends(new T(...)): the typed signal a button, field or nav item sends.
    [GeneratedRegex(@"\bSends\(new (?!Command\()(?<type>[A-Z][A-Za-z0-9_]*)\(")]
    private static partial Regex TypedCommand();

    [GeneratedRegex(""""(?<helper>BrainEmit|DesktopEmit)\((?<signal>"[a-z-]+"|[^,)]+)|(?<helper>DesktopButton)\([^,]+,\s*[^,]+,\s*(?<signal>"[a-z-]+"|[^,)]+)|(?<helper>DesktopIcon)\([^,]+,\s*[^,]+,\s*[^,]+,\s*(?<signal>"[a-z-]+"|[^,)]+)"""")]
    private static partial Regex HostEmit();
}
