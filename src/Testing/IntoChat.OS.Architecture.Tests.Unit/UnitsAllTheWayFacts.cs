using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Units all the way, the fences the other lane facts do not already hold: NoProgramLanesFacts (no IAppRuntime / sandbox / Microsoft.CSharp
// module / "__" setting keys), OneUiPathFacts (the kept UI kinds and the UiNode / _ui / ButtonAction ban), NoLegacyWorkMechanismsFacts
// (deleted names), DartNodeModelsFacts (generated Dart models), IAppSurfaceFacts and the Dart vocabulary mirror. What is here is what none
// of those scan: who may author code at runtime, which hosting code names an app, and whether every node kind has a Dart renderer.
public sealed partial class UnitsAllTheWayFacts
{
    // Roslyn is the Roslyn module's product (workspace queries and edits for developer tooling); it never compiles a unit.
    private const string RoslynModule = "IntoChat.OS.Modules.Microsoft.Roslyn";

    // The only production code that holds the SynapseCompiler: the Creator's Builder (check_unit), the Apps module's authoring helpers
    // that run the same analyzers, the build flow that verifies a unit before it installs, and Ino's synapse studio tool.
    private static readonly string[] CompilerCallers =
    [
        "Apps/Creator.cs",
        "Authoring/BuilderTools.cs",
        "Authoring/SynapseCheck.cs",
        "Flows/BuildFlow.cs",
        "Ino/SynapseStudioNeuron.cs",
    ];

    [Fact]
    public void NoRuntimeAuthoringOutsideSynapseSources()
    {
        var offenders = new List<string>();
        foreach (var path in NoProgramLanesFacts.ProductionSources())
        {
            var relative = Repository.Relative(path);
            var text = File.ReadAllText(path);
            if (!relative.Contains("/" + RoslynModule + "/", StringComparison.Ordinal) && text.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            { offenders.Add(relative + ": Microsoft.CodeAnalysis"); }
            foreach (Match match in RuntimeCompilation().Matches(text)) { offenders.Add(relative + ": " + match.Value); }
            if (SynapseCompilerUse().IsMatch(text) && !CompilerCallers.Any(caller => relative.EndsWith("/" + caller, StringComparison.Ordinal)))
            { offenders.Add(relative + ": SynapseCompiler"); }
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void RoslynPackagesAreReferencedOnlyByTheRoslynModule()
    {
        var offenders = Repository.Projects.Values
            .Where(project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && project.Name != RoslynModule)
            .Where(project => Repository.PackageReferences(project.Path).Any(package => package.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)))
            .Select(project => project.Name)
            .Order(StringComparer.Ordinal);
        Assert.Empty(offenders);
    }

    // The shell's own apps: the desktop and the workspace are the host's surfaces, not apps a person installs, so the host may name them.
    // Any other app is reached through its manifest, its signals and the registry, never by name.
    private static readonly string[] ShellApps = ["desktop", "workspace"];

    // What the host still names today (file: literal). Each entry is a leftover to remove by moving the knowledge into the app's manifest;
    // the fact fails when an entry is gone, so the list only shrinks.
    private static readonly string[] Pending =
    [
        "AppPresentationSink.cs: creator", "AppPresentationSink.cs: files", "AppPresentationSink.cs: filmcraft",
        "AppPresentationSink.cs: gallery", "AppPresentationSink.cs: photocraft", "AppPresentationSink.cs: settings",
        "CompiledAppsHost.cs: circle", "CompiledAppsHost.cs: circles", "CompiledAppsHost.cs: creator", "CompiledAppsHost.cs: data",
        "CompiledAppsHost.cs: files", "CompiledAppsHost.cs: filmcraft", "CompiledAppsHost.cs: gallery", "CompiledAppsHost.cs: ino",
        "CompiledAppsHost.cs: markdraw", "CompiledAppsHost.cs: photocraft", "CompiledAppsHost.cs: post", "CompiledAppsHost.cs: settings",
        "CompiledAppsHost.cs: tasks",
    ];

    [Fact]
    public void NoAppNameSwitchboard()
    {
        var found = HostingNamedApps().Where(entry => !ShellApps.Contains(entry.Literal, StringComparer.Ordinal))
            .Select(entry => entry.File + ": " + entry.Literal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(Pending.Order(StringComparer.Ordinal).SequenceEqual(found), string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void TheAppNameScanSeesAComparisonAndASwitchArm()
    {
        Assert.Equal(["ino", "files"], NamedApps("if (appName == \"ino\" || other) { } var t = appId switch { \"files\" => 1, _ => 0 };"));
        Assert.Equal(["a", "b"], NamedApps("if (app is not \"a\" or \"b\") { }"));
        Assert.Empty(NamedApps("if (mode == \"fast\") { } var x = kind switch { \"a\" => 1, _ => 0 };"));
    }

    private static IEnumerable<(string File, string Literal)> HostingNamedApps()
        => NoProgramLanesFacts.ProductionSources().Where(path => Repository.Relative(path).Contains("/Hosting/", StringComparison.Ordinal))
            .SelectMany(path => NamedApps(File.ReadAllText(path)).Select(literal => (Path.GetFileName(path), literal)));

    private static string[] NamedApps(string source)
    {
        var literals = new List<string>();
        foreach (Match match in AppComparison().Matches(source)) { literals.AddRange(Literals(match.Groups["lits"].Value)); }
        foreach (Match match in LiteralFirstComparison().Matches(source)) { literals.Add(match.Groups["lit"].Value); }
        foreach (Match match in AppSwitch().Matches(source))
        {
            var open = match.Index + match.Length;
            var depth = 1;
            var end = open;
            while (end < source.Length && depth > 0) { depth += source[end] == '{' ? 1 : source[end] == '}' ? -1 : 0; end++; }
            literals.AddRange(SwitchArm().Matches(source[open..end]).SelectMany(arm => Literals(arm.Groups["lits"].Value)));
        }
        return [.. literals];
    }

    private static IEnumerable<string> Literals(string text) => QuotedLiteral().Matches(text).Select(match => match.Groups[1].Value);

    [Fact]
    public void EveryNodeKindHasADartRenderer()
    {
        var nodes = Path.Combine(Repository.Root, "ui/ui/lib/src/nodes");
        var dispatch = File.ReadAllText(Path.Combine(nodes, "node_renderer.dart"));
        var missing = new List<string>();
        foreach (var (type, kind) in DartNodeModelsFacts.DerivedKinds())
        {
            var dart = DartNodeModelsFacts.DartName(type);
            var render = "render" + dart[..^"Node".Length];
            // One file per kind named after it (splitPane is split_pane.dart), holding the renderer the dispatch calls.
            var file = Path.Combine(nodes, Regex.Replace(kind, "(?<=[a-z])([A-Z])", "_$1").ToLowerInvariant() + ".dart");
            if (!File.Exists(file)) { missing.Add($"{kind}: no {Path.GetFileName(file)}"); continue; }
            if (!File.ReadAllText(file).Contains(render + "(", StringComparison.Ordinal)) { missing.Add($"{kind}: {Path.GetFileName(file)} has no {render}"); }
            if (!dispatch.Contains($"{dart}() => {render}(", StringComparison.Ordinal)) { missing.Add($"{kind}: node_renderer.dart does not dispatch {dart}"); }
        }
        Assert.Empty(missing);
    }

    [GeneratedRegex(@"\b(CSharpCompilation|CSharpScript|AssemblyLoadContext|Assembly\s*\.\s*(LoadFrom|LoadFile))\b", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeCompilation();

    [GeneratedRegex(@"\bSynapseCompiler\b", RegexOptions.CultureInvariant)]
    private static partial Regex SynapseCompilerUse();

    [GeneratedRegex(@"\b\w*[aA]pp\w*(?:\.\w+)*\s*(?:==|!=|\bis\s+not|\bis)\s*(?<lits>""[^""]*""(?:\s+or\s+""[^""]*"")*)", RegexOptions.CultureInvariant)]
    private static partial Regex AppComparison();

    [GeneratedRegex(@"(?<![\w.])""(?<lit>[^""]*)""\s*(?:==|!=)\s*\w*[aA]pp\w*\b", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralFirstComparison();

    [GeneratedRegex(@"\b\w*[aA]pp\w*\s+switch\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex AppSwitch();

    [GeneratedRegex(@"(?<lits>""[^""]*""(?:\s+or\s+""[^""]*"")*)\s*=>", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchArm();

    [GeneratedRegex(@"""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedLiteral();
}
