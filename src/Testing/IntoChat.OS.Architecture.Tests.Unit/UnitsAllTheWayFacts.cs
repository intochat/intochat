using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Apps;

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
        var sources = NoProgramLanesFacts.ProductionSources().ToArray();
        Assert.NotEmpty(sources);
        Assert.Contains(sources, path => Repository.Relative(path).Contains("/" + RoslynModule + "/", StringComparison.Ordinal));
        var calling = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in sources)
        {
            var relative = Repository.Relative(path);
            var text = File.ReadAllText(path);
            if (!relative.Contains("/" + RoslynModule + "/", StringComparison.Ordinal) && text.Contains("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            { offenders.Add(relative + ": Microsoft.CodeAnalysis"); }
            foreach (Match match in RuntimeCompilation().Matches(text)) { offenders.Add(relative + ": " + match.Value); }
            if (!SynapseCompilerUse().IsMatch(text)) { continue; }
            if (CompilerCallers.FirstOrDefault(caller => relative.EndsWith("/" + caller, StringComparison.Ordinal)) is { } known) { calling.Add(known); }
            else { offenders.Add(relative + ": SynapseCompiler"); }
        }
        // Every allowed caller exists and still holds the compiler; an entry that matches nothing is stale.
        offenders.AddRange(CompilerCallers.Except(calling).Select(caller => caller + ": allowed but no longer uses SynapseCompiler"));
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void RoslynPackagesAreReferencedOnlyByTheRoslynModule()
    {
        Assert.True(Repository.Projects.ContainsKey(RoslynModule), RoslynModule + " is not in the solution; update this fact.");
        var offenders = Repository.Projects.Values
            .Where(project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && project.Name != RoslynModule)
            .Where(project => Repository.PackageReferences(project.Path).Any(package => package.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)))
            .Select(project => project.Name)
            .Order(StringComparer.Ordinal);
        Assert.Empty(offenders);
    }

    // The shell's own apps: the desktop and the workspace are the host's surfaces and ino is the shell's agent, not apps a person installs,
    // so the host may name them.
    // Any other app is reached through its manifest, its signals and the registry, never by name.
    private static readonly string[] ShellApps = ["desktop", "workspace", "ino", "friends"];

    [Fact]
    public void NoAppNameSwitchboard()
    {
        var found = HostingNamedApps().Where(entry => !ShellApps.Contains(entry.Literal, StringComparer.Ordinal))
            .Select(entry => entry.File + ": " + entry.Literal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(found.Length == 0, string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void TheOwnerScopedScanFlagsALiteralOutsideItsOwnerAndNotAnAppNameReference()
    {
        HashSet<string> keys = new(["photocraft", "settings"], StringComparer.Ordinal);
        Assert.Equal(["photocraft"], Violations("var k = IApp.Key(brain, \"photocraft\");", "Mod/Hosting/Host.cs", keys));
        Assert.Empty(Violations("var k = IApp.Key(brain, PhotoCraftApp.AppName);", "Mod/Hosting/Host.cs", keys));
        Assert.Empty(Violations("var k = IApp.Key(brain, \"photocraft\");", "IntoChat.OS.Apps/PhotoCraft/PhotoCraftApp.cs", keys));
        Assert.Empty(Violations("var k = Open(\"desktop\", \"ino\");", "Mod/Hosting/Host.cs", keys));
        var verbs = WithoutOperationPositions("DesktopButton(\"more\", \"Settings\", \"settings\", null); AppSignals.Emit(App, \"settings\"); X = Open(\"settings\");");
        Assert.Equal(["settings"], Violations(verbs, "Mod/Hosting/Host.cs", keys));
    }

    [Fact]
    public void TheAppNameScanSeesAComparisonAndASwitchArm()
    {
        Assert.Equal(["ino", "files"], NamedApps("if (appName == \"ino\" || other) { } var t = appId switch { \"files\" => 1, _ => 0 };"));
        Assert.Equal(["a", "b"], NamedApps("if (app is not \"a\" or \"b\") { }"));
        Assert.Empty(NamedApps("if (mode == \"fast\") { } var x = kind switch { \"a\" => 1, _ => 0 };"));
    }

    // Every shipped app's key: the name of each compiled [App] bundle in the production assemblies.
    private static HashSet<string> ShippedAppKeys() => ProductionArchitecture.Types
        .Select(type => type.GetProperty("Bundle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        .Where(property => property?.PropertyType == typeof(AppBundle))
        .Select(property => ((AppBundle)property!.GetValue(null)!).Name)
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(string File, string Literal)> HostingNamedApps()
    {
        var hosting = NoProgramLanesFacts.ProductionSources().Where(path => Repository.Relative(path).Contains("/Hosting/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(hosting);
        var keys = ShippedAppKeys();
        Assert.Contains("creator", keys);
        Assert.Contains("settings", keys);
        // A comparison or switch arm on an app identifier, or a quoted literal that is a shipped app's key outside that app's own folder.
        // Operation and signal names are not app keys, so those positions are blanked first; `X.AppName` is a reference, not a literal.
        return hosting.SelectMany(path =>
        {
            var text = WithoutOperationPositions(File.ReadAllText(path));
            return Violations(text, Repository.Relative(path), keys).Select(literal => (Path.GetFileName(path), literal));
        });
    }

    internal static string[] Violations(string text, string relativePath, HashSet<string> keys)
        => [.. NamedApps(text).Concat(QuotedLiteral().Matches(text).Select(match => match.Groups[1].Value).Where(keys.Contains))
            .Where(literal => !ShellApps.Contains(literal, StringComparer.Ordinal))
            .Where(literal => !relativePath.Split('/').Contains(literal, StringComparer.OrdinalIgnoreCase))];

    // The literal in a signal's or operation's position names a verb, not an app: AppSignals.Emit's second argument, the desktop's emit helpers
    // and its window-verb set.
    internal static string WithoutOperationPositions(string text)
    {
        text = DesktopVerbSet().Replace(text, "");
        foreach (var position in OperationPositions()) { text = position.Replace(text, match => match.Value.Replace("\"" + match.Groups["verb"].Value + "\"", "\"\"")); }
        return text;
    }

    [GeneratedRegex(@"DesktopWindowVerbs\s*=[^;]*;", RegexOptions.CultureInvariant)]
    private static partial Regex DesktopVerbSet();

    private static Regex[] OperationPositions() =>
    [
        new(@"AppSignals\.Emit\(\s*[^,()]+,\s*""(?<verb>[^""]*)""", RegexOptions.CultureInvariant),
        new(@"DesktopEmit\(\s*""(?<verb>[^""]*)""", RegexOptions.CultureInvariant),
        new(@"DesktopButton\(\s*""[^""]*""\s*,\s*""[^""]*""\s*,\s*""(?<verb>[^""]*)""", RegexOptions.CultureInvariant),
        new(@"DesktopIcon\(\s*""[^""]*""\s*,\s*""[^""]*""\s*,\s*""[^""]*""\s*,\s*""(?<verb>[^""]*)""", RegexOptions.CultureInvariant),
    ];

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

    // The files that may reference a ShippedApps key constant: the declaration, the apps that alias it as their AppName, and the hosts
    // that must name a shipped app (the ArtCraft engine checks its studios' calls; the installed-modules grant names Settings; the
    // shipped grants are per app; the files workspace hands a file to Ino; an app's installation mounts the account surface for
    // Settings).
    private static readonly string[] KeyReferenceFiles =
    [
        "ShippedApps.cs", "SettingsApp.cs", "PhotoCraftApp.cs", "FilmCraftApp.cs", "CreatorApp.cs",
        "ArtCraftEngineNeuron.cs", "ArtCraftEngineRunner.cs", "InstalledModulesNeuron.cs",
        "ShippedGrants.cs", "FilesWorkspaceNeuron.cs", "AppNeuron.Installation.cs",
    ];

    [Fact]
    public void OnlyTheListedFilesReferenceAShippedAppKeyConstant()
    {
        var referencing = NoProgramLanesFacts.ProductionSources()
            .Where(path => ShippedAppKey().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetFileName(path))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(KeyReferenceFiles.Order(StringComparer.Ordinal), referencing);
    }

    [GeneratedRegex(@"\bShippedApps\s*\.\s*(Settings|PhotoCraft|FilmCraft|Creator|Ino)\b|const string (Settings|PhotoCraft|FilmCraft|Creator|Ino)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ShippedAppKey();

    // The desktop's window verbs are blanked before the app-key scan, so they are pinned here: each must be an operation the desktop app
    // declares, which is what makes "settings" there a verb and not the settings app.
    [Fact]
    public void TheDesktopWindowVerbsAreOperationsOfTheDesktopApp()
    {
        var host = NoProgramLanesFacts.ProductionSources().Single(path => Path.GetFileName(path) == "DesktopApp.cs");
        var set = DesktopVerbSet().Match(File.ReadAllText(host)).Value;
        var verbs = QuotedLiteral().Matches(set).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["close", "close-app", "favourite", "focus", "newtab", "pin", "settings", "tab", "unpin", "widget-close", "widget-open"], verbs);
        var desktop = ProductionArchitecture.Types
            .Select(type => type.GetProperty("Bundle", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Where(property => property?.PropertyType == typeof(AppBundle))
            .Select(property => (AppBundle)property!.GetValue(null)!)
            .Single(bundle => bundle.Name == "desktop");
        Assert.Empty(verbs.Except(desktop.Manifest.Operations.Select(operation => operation.Name)));
    }

    [Fact]
    public void EveryNodeKindHasADartRenderer()
    {
        var nodes = Path.Combine(Repository.Root, "ui/ui/lib/src/nodes");
        var dispatch = File.ReadAllText(Path.Combine(nodes, "node_renderer.dart"));
        var missing = new List<string>();
        var kinds = DartNodeModelsFacts.DerivedKinds();
        Assert.NotEmpty(kinds);
        Assert.NotEmpty(Directory.EnumerateFiles(nodes, "*.dart"));
        foreach (var (type, kind) in kinds)
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
