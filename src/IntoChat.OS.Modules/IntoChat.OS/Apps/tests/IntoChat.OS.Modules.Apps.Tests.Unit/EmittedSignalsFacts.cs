using System.Reflection;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// A view's emit is a request: AppNeuron refuses a signal its app's manifest does not declare. This scans the source of the compiled
// apps (the IntoChat.OS.Apps project) and of the host-built views in the Apps module (Files, the brain scene, the desktop shell) for every
// literal emitted signal and compares it with the emitting app's Bundle.Manifest.Operations. A site whose owner or signal is not a
// literal cannot be checked here; it is reported in the output, not skipped silently.
public sealed partial class EmittedSignalsFacts
{
    [Fact]
    public void EveryEmittedSignalOfACompiledAppIsDeclaredInItsManifest()
    {
        var root = RepositoryRoot();
        var operations = Manifests();
        var undeclared = new List<string>();
        var unchecked_ = new List<string>();
        var found = 0;

        foreach (var file in Directory.EnumerateFiles(ProjectDirectory(root, "IntoChat.OS.Apps"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(ProjectDirectory(root, "IntoChat.OS.Modules.Apps"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            var app = AppConstant().Match(source) is { Success: true } constant ? constant.Groups[1].Value : null;
            var name = Path.GetRelativePath(root, file);
            foreach (var (owner, signal, site) in Sites(source, app, name))
            {
                if (owner is null || signal is null) { unchecked_.Add(site); continue; }
                found++;
                if (!operations.TryGetValue(owner, out var declared)) { undeclared.Add($"{site}: no manifest for app '{owner}'"); }
                else if (!declared.Contains(signal)) { undeclared.Add($"{site}: '{owner}' does not declare '{signal}'"); }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, unchecked_.Select(site => "Not a literal emit, unchecked: " + site)));
        Assert.True(found > 50, $"Only {found} literal emits found; the scan patterns no longer match the source.");
        Assert.True(undeclared.Count == 0, string.Join(Environment.NewLine, undeclared));
    }

    // (owner, signal, site) for every emit call; a null owner or signal is a non-literal site.
    private static IEnumerable<(string? Owner, string? Signal, string Site)> Sites(string source, string? app, string file)
    {
        string Site(Match match) => $"{file}:{source[..match.Index].Count(c => c == '\n') + 1}: {match.Value.Trim()}";
        string? Literal(string argument) => argument.StartsWith('"') ? argument.Trim('"') : argument == "App" ? app : null;

        foreach (Match match in GeneralEmit().Matches(source))
        {
            yield return (Literal(match.Groups["owner"].Value), match.Groups["signal"].Value is { Length: > 0 } s && s.StartsWith('"') ? s.Trim('"') : null, Site(match));
        }
        foreach (Match match in FilesAct().Matches(source))
        {
            var signal = match.Groups["signal"].Value;
            yield return ("files", signal.StartsWith('"') ? Normalize(signal.Trim('"')) : null, Site(match));
        }
        if (file.EndsWith("FilesScreen.cs", StringComparison.Ordinal) || file.EndsWith("FilesWorkspaceView.cs", StringComparison.Ordinal))
        {
            foreach (Match match in FilesButton().Matches(source)) { yield return ("files", Normalize(match.Groups["signal"].Value), Site(match)); }
            foreach (Match match in LocalAct().Matches(source)) { yield return ("files", Normalize(match.Groups["signal"].Value), Site(match)); }
        }
        foreach (Match match in HostEmit().Matches(source))
        {
            var owner = match.Groups["helper"].Value.StartsWith("Brain", StringComparison.Ordinal) ? "brain" : "desktop";
            var signal = match.Groups["signal"].Value;
            yield return (owner, signal.StartsWith('"') ? signal.Trim('"') : null, Site(match));
        }
    }

    // FilesUi.Act sends search-all as search.
    private static string Normalize(string files) => files == "search-all" ? "search" : files;

    private static Dictionary<string, HashSet<string>> Manifests()
    {
        var bundles = typeof(IntoChat.OS.Apps.AppSignals).Assembly.GetTypes()
            .Select(type => type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null))
            .OfType<AppBundle>()
            .Append(FilesRequests.Bundle)
            .Append(BrainScene.Bundle);
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

    [GeneratedRegex(""""const string App = "([a-z-]+)";"""")]
    private static partial Regex AppConstant();

    // AppSignals.Emit(owner, signal...) and new Emit(owner, signal...): owner a literal or App, signal any first argument.
    [GeneratedRegex(""""(?<![\\"])(?:AppSignals\.Emit|new Emit)\((?<owner>"[a-z-]+"|[A-Za-z_][A-Za-z0-9_.]*),\s*(?<signal>"[a-z-]+"|[^,)]+)"""")]
    private static partial Regex GeneralEmit();

    [GeneratedRegex(""""FilesUi\.Act\((?:rev|[a-z]+),\s*(?<signal>"[a-z-]+"|[^,)]+)"""")]
    private static partial Regex FilesAct();

    // FilesScreen/FilesWorkspaceView's local Button(key, text, "verb", ...) helpers.
    [GeneratedRegex(@"(?<![.\w])Button\(""[a-z0-9-]+"",\s*[^,]+,\s*""(?<signal>[a-z-]+)""")]
    private static partial Regex FilesButton();

    [GeneratedRegex(@"(?<![.\w])Act\(""(?<signal>[a-z-]+)""")]
    private static partial Regex LocalAct();

    [GeneratedRegex(""""(?<helper>BrainEmit|DesktopEmit)\((?<signal>"[a-z-]+"|[^,)]+)|(?<helper>DesktopButton)\([^,]+,\s*[^,]+,\s*(?<signal>"[a-z-]+"|[^,)]+)|(?<helper>DesktopIcon)\([^,]+,\s*[^,]+,\s*[^,]+,\s*(?<signal>"[a-z-]+"|[^,)]+)"""")]
    private static partial Regex HostEmit();
}
