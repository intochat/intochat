using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A component is one packaging unit under src/Components/<Name>: the implementation project
// IntoChat.<Name>.csproj and its Contracts/IntoChat.<Name>.Contracts.csproj. It replaces the module
// (src/Modules/<Name>) and the app (src/IntoChat.OS.Apps/<App>) one at a time, so the components root can hold
// no folder yet and every rule here reads as a finding list that is empty on an empty root. Each rule is a function
// of a root, so a challenger runs it on a sample and proves it can fail.
public sealed partial class ComponentFacts
{
    // Component implementation projects that may reference another component's or module's implementation project.
    // Each entry is (component project, referenced implementation project) and is mirrored by an OWED.md line naming both.
    internal static readonly (string Project, string Referenced)[] AllowedImplementationReferences = [];

    internal const int AllowedImplementationReferencesCap = 0;

    // [Neuron("app.<key>")] declarations still in the IntoChat.OS.Apps assembly; each one moves into a component's Screens.
    internal const int AppsLeftCap = 33;

    private const string OwedFile = "OWED.md";

    [Fact]
    public void AComponentHasOneImplementationProjectAndOneContractsProject()
    {
        var findings = LayoutFindings(Repository.ComponentsRoot);
        Assert.True(findings.Count == 0,
            "A component folder holds IntoChat.<Name>.csproj and Contracts/IntoChat.<Name>.Contracts.csproj, and no other project:\n" + string.Join('\n', findings));
    }

    [Fact]
    public void AComponentHasUnitTestsUnderTestsComponents()
    {
        var unit = Path.Combine(Repository.Root, "tests", "IntoChat.OS.Tests.Unit", "Components");
        var untested = UntestedComponents(Repository.ComponentsRoot, unit);
        Assert.True(untested.Count == 0, "Components without unit tests under tests/IntoChat.OS.Tests.Unit/Components/<Name>: " + string.Join(", ", untested));
    }

    [Fact]
    public void UnitTestsChallengerFlagsAComponentWithoutATestFolder()
    {
        using var sample = new Sample();
        sample.Write("components/Tested/IntoChat.Tested.csproj", "<Project />");
        sample.Write("components/Untested/IntoChat.Untested.csproj", "<Project />");
        sample.Write("unit/Tested/SomeFacts.cs", "class SomeFacts;");

        var untested = UntestedComponents(Path.Combine(sample.Root, "components"), Path.Combine(sample.Root, "unit"));

        Assert.Equal(["Untested"], untested);
        Assert.Empty(UntestedComponents(Path.Combine(sample.Root, "none"), Path.Combine(sample.Root, "unit")));
    }

    private static List<string> UntestedComponents(string componentsRoot, string unitTestsRoot)
        => !Directory.Exists(componentsRoot) ? []
            : [.. Directory.EnumerateDirectories(componentsRoot).Select(Path.GetFileName).OfType<string>()
                .Where(name => name is not ("bin" or "obj") && !Directory.Exists(Path.Combine(unitTestsRoot, name)))
                .Order(StringComparer.Ordinal)];

    [Fact]
    public void AComponentReferencesOtherComponentsOnlyThroughContracts()
    {
        Assert.True(AllowedImplementationReferences.Length == AllowedImplementationReferencesCap, "AllowedImplementationReferencesCap must equal the length of AllowedImplementationReferences");
        var owed = File.ReadAllText(Path.Combine(Repository.Root, OwedFile));
        var findings = ReferenceFindings(Repository.ComponentsRoot, Repository.ModuleRoots, AllowedImplementationReferences, owed);
        Assert.True(findings.Count == 0,
            "A component implementation project references no module or component project except a *.Contracts one; an allowance is listed in AllowedImplementationReferences and OWED.md:\n"
            + string.Join('\n', findings));
    }

    // The package allow-list packs IntoChat.OS* only (PackageRootAllowlistFacts), so a component's packages are
    // IntoChat.OS.Components.<Name> and IntoChat.OS.Components.<Name>.Contracts while its assemblies are IntoChat.<Name>.
    [Fact]
    public void AComponentsPackageIdsAreIntoChatOSComponentsNames()
    {
        var findings = PackageIdFindings(Repository.ComponentsRoot);
        Assert.True(findings.Count == 0,
            "A component's PackageId is IntoChat.OS.Components.<Name> (implementation) or IntoChat.OS.Components.<Name>.Contracts:\n" + string.Join('\n', findings));
    }

    [Fact]
    public void AComponentScreenNamesNoOtherComponentsContract()
    {
        var findings = ScreenUsingFindings(Repository.ComponentsRoot);
        Assert.True(findings.Count == 0,
            "A component's Screens use only its own namespaces, IntoChat.OS.UI, IntoChat.OS, IntoChat.OS.Host, IntoChat.OS.Modules.Apps, DigitalBrain, System and Microsoft.Extensions:\n"
            + string.Join('\n', findings));
    }

    // A component's Hosting folder is AppHost code (it references Aspire.Hosting): the component's runtime assembly must
    // not compile it, and the AppHost compiles it through a linked item.
    [Fact]
    public void AComponentsHostingIsCompiledOnlyIntoTheAppHost()
    {
        var findings = HostingFindings(Repository.ComponentsRoot, Repository.ProjectNamed("IntoChat.OS.AppHost").Path);
        Assert.True(findings.Count == 0, "A component's Hosting folder is removed from its own compile items and included by the AppHost:\n" + string.Join('\n', findings));
        Assert.False(
            Directory.Exists(Path.Combine(Repository.ProjectNamed("IntoChat.OS.AppHost").Directory, "Hosting", "Modules", "Microsoft.Aspire")),
            "The Aspire hosting code lives in src/Components/Aspire/Hosting; the old AppHost folder Hosting/Modules/Microsoft.Aspire must not return");
    }

    [Fact]
    public void AppsLeftInTheAppsAssembly()
    {
        var appsDirectory = Repository.ProjectNamed("IntoChat.OS.Apps").Directory;
        var message = RatchetFinding(CountAppNeurons(appsDirectory), AppsLeftCap);
        Assert.True(message is null, message);
    }

    [Fact]
    public void ComponentsRootIsBesideModulesRoot()
    {
        Assert.Equal(Path.GetDirectoryName(Repository.ModulesRoot), Path.GetDirectoryName(Repository.ComponentsRoot));
        Assert.True(File.Exists(Path.Combine(Repository.ComponentsRoot, "Directory.Build.props")), "The components root needs its own Directory.Build.props");
        Assert.Equal(
            XDocument.Load(Path.Combine(Repository.ModulesRoot, "Directory.Build.props")).ToString(),
            XDocument.Load(Path.Combine(Repository.ComponentsRoot, "Directory.Build.props")).ToString());
    }

    // Challengers: each rule runs on a sample and must flag it, and must pass a clean one.

    [Fact]
    public void LayoutChallengerFlagsAMissingContractsProjectAndAStrayProject()
    {
        using var sample = new Sample();
        sample.Write("Good/IntoChat.Good.csproj", "<Project />");
        sample.Write("Good/Contracts/IntoChat.Good.Contracts.csproj", "<Project />");
        sample.Write("Good/obj/IntoChat.Ignored.csproj", "<Project />");
        sample.Write("NoContracts/IntoChat.NoContracts.csproj", "<Project />");
        sample.Write("Stray/IntoChat.Stray.csproj", "<Project />");
        sample.Write("Stray/Contracts/IntoChat.Stray.Contracts.csproj", "<Project />");
        sample.Write("Stray/Other/IntoChat.Stray.Other.csproj", "<Project />");
        sample.Write("Misnamed/IntoChat.Other.csproj", "<Project />");
        sample.Write("Misnamed/Contracts/IntoChat.Misnamed.Contracts.csproj", "<Project />");

        var findings = LayoutFindings(sample.Root);

        Assert.DoesNotContain(findings, finding => finding.StartsWith("Good", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("NoContracts/Contracts/IntoChat.NoContracts.Contracts.csproj", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Stray/Other/IntoChat.Stray.Other.csproj", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Misnamed/IntoChat.Misnamed.csproj", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Misnamed/IntoChat.Other.csproj", StringComparison.Ordinal));
        Assert.Empty(LayoutFindings(Path.Combine(sample.Root, "does-not-exist")));
    }

    [Fact]
    public void PackageIdChallengerFlagsAMissingOrWrongIdAndAcceptsTheRuleOnes()
    {
        using var sample = new Sample();
        static string Package(string id) => $"<Project><PropertyGroup><PackageId>{id}</PackageId></PropertyGroup></Project>";
        sample.Write("Good/IntoChat.Good.csproj", Package("IntoChat.OS.Components.Good"));
        sample.Write("Good/Contracts/IntoChat.Good.Contracts.csproj", Package("IntoChat.OS.Components.Good.Contracts"));
        sample.Write("Bare/IntoChat.Bare.csproj", "<Project />");
        sample.Write("Wrong/IntoChat.Wrong.csproj", Package("IntoChat.OS.Wrong"));
        sample.Write("Wrong/Contracts/IntoChat.Wrong.Contracts.csproj", Package("IntoChat.OS.Components.Wrong"));

        var findings = PackageIdFindings(sample.Root);

        Assert.DoesNotContain(findings, finding => finding.StartsWith("Good", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith("Bare/IntoChat.Bare.csproj", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith("Wrong/IntoChat.Wrong.csproj", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith("Wrong/Contracts/IntoChat.Wrong.Contracts.csproj", StringComparison.Ordinal));
        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public void ReferenceChallengerFlagsAnImplementationReferenceAndKeepsAllowancesHonest()
    {
        using var sample = new Sample();
        var modules = Path.Combine(sample.Root, "modules");
        sample.Write("modules/Old/IntoChat.OS.Modules.Old.csproj", "<Project />");
        sample.Write("modules/Old/Contracts/IntoChat.OS.Modules.Old.Contracts.csproj", "<Project />");
        sample.Write("components/Alpha/IntoChat.Alpha.csproj", References("Contracts/IntoChat.Alpha.Contracts.csproj", "../Beta/Contracts/IntoChat.Beta.Contracts.csproj", "../../IntoChat.OS/IntoChat.OS.csproj", "$(RepoRoot)/framework/DigitalBrain.csproj"));
        sample.Write("components/Alpha/Contracts/IntoChat.Alpha.Contracts.csproj", "<Project />");
        sample.Write("components/Beta/IntoChat.Beta.csproj", "<Project />");
        sample.Write("components/Beta/Contracts/IntoChat.Beta.Contracts.csproj", "<Project />");
        var roots = new[] { modules, Path.Combine(sample.Root, "components") };
        var components = roots[1];

        Assert.Empty(ReferenceFindings(components, roots, [], ""));

        sample.Write("components/Alpha/IntoChat.Alpha.csproj", References("Contracts/IntoChat.Alpha.Contracts.csproj", "../Beta/IntoChat.Beta.csproj", "../../modules/Old/IntoChat.OS.Modules.Old.csproj"));
        var unlisted = ReferenceFindings(components, roots, [], "");
        Assert.Contains(unlisted, finding => finding.Contains("IntoChat.Alpha -> IntoChat.Beta", StringComparison.Ordinal));
        Assert.Contains(unlisted, finding => finding.Contains("IntoChat.Alpha -> IntoChat.OS.Modules.Old", StringComparison.Ordinal));

        (string, string)[] allowed = [("IntoChat.Alpha", "IntoChat.Beta"), ("IntoChat.Alpha", "IntoChat.OS.Modules.Old")];
        var noOwed = ReferenceFindings(components, roots, allowed, "- Something else entirely.");
        Assert.Equal(2, noOwed.Count);
        Assert.All(noOwed, finding => Assert.Contains("OWED.md", finding, StringComparison.Ordinal));

        var owed = "- `IntoChat.Alpha` still references `IntoChat.Beta`; move it behind a contract. Owner: the repo owner.\n"
            + "- `IntoChat.Alpha` still references `IntoChat.OS.Modules.Old`; Owner: the repo owner.";
        Assert.Empty(ReferenceFindings(components, roots, allowed, owed));

        var stale = ReferenceFindings(components, roots, [.. allowed, ("IntoChat.Alpha", "IntoChat.Gone")], owed + "\n- `IntoChat.Alpha` `IntoChat.Gone`");
        Assert.Single(stale);
        Assert.Contains("stale", stale[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenChallengerFlagsAnotherComponentsNamespaceAndAcceptsTheAllowedOnes()
    {
        using var sample = new Sample();
        sample.Write("Alpha/Screens/Good.cs", """
            using System.Text;
            using Microsoft.Extensions.Logging;
            using DigitalBrain.Units;
            using IntoChat.OS;
            using IntoChat.OS.UI.Kit;
            using IntoChat.OS.Host.Surface;
            using IntoChat.OS.Modules.Apps;
            using IntoChat.Alpha;
            using IntoChat.Alpha.Neurons;
            using static IntoChat.Alpha.Constants;
            namespace IntoChat.Alpha.Screens;
            """);
        sample.Write("Alpha/Screens/Bad.cs", """
            using IntoChat.Beta;
            using IntoChat.AlphaTwo;
            using IntoChat.OS.Modules.Azure;
            using IntoChat.OS.Kernel;
            using Contract = IntoChat.Beta.Contracts.IBeta;
            namespace IntoChat.Alpha.Screens;
            """);
        sample.Write("Alpha/Neurons/Elsewhere.cs", "using IntoChat.Beta;");

        var findings = ScreenUsingFindings(sample.Root);

        Assert.DoesNotContain(findings, finding => finding.Contains("Good.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, finding => finding.Contains("Elsewhere.cs", StringComparison.Ordinal));
        Assert.Equal(5, findings.Count(finding => finding.Contains("Bad.cs", StringComparison.Ordinal)));
        Assert.Contains(findings, finding => finding.Contains("Bad.cs", StringComparison.Ordinal) && finding.Contains("using IntoChat.Beta;", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("using IntoChat.AlphaTwo;", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("using IntoChat.Beta.Contracts.IBeta", StringComparison.Ordinal));
    }

    [Fact]
    public void HostingChallengerFlagsAComponentThatCompilesItsHostingOrIsNotIncludedByTheAppHost()
    {
        using var sample = new Sample();
        sample.Write("Alpha/Hosting/AlphaHosting.cs", "class AlphaHosting;");
        sample.Write("Alpha/IntoChat.Alpha.csproj", "<Project />");
        sample.Write("Beta/Hosting/BetaHosting.cs", "class BetaHosting;");
        sample.Write("Beta/IntoChat.Beta.csproj", "<Project><ItemGroup><Compile Remove=\"Contracts/**;Hosting/**\" /></ItemGroup></Project>");
        sample.Write("Gamma/IntoChat.Gamma.csproj", "<Project />");
        sample.Write("AppHost.csproj", "<Project><ItemGroup><Compile Include=\"../Components/Beta/Hosting/**/*.cs\" /></ItemGroup></Project>");
        var appHost = Path.Combine(sample.Root, "AppHost.csproj");

        var findings = HostingFindings(sample.Root, appHost);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.Contains("Alpha", StringComparison.Ordinal) && finding.Contains("Remove", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Alpha", StringComparison.Ordinal) && finding.Contains("AppHost", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, finding => finding.Contains("Beta", StringComparison.Ordinal) || finding.Contains("Gamma", StringComparison.Ordinal));
        Assert.Empty(HostingFindings(Path.Combine(sample.Root, "does-not-exist"), appHost));
    }

    [Fact]
    public void AppsChallengerCountsNeuronsAndTheRatchetFailsBothWays()
    {
        using var sample = new Sample();
        sample.Write("One/OneApp.cs", "[Neuron(\"app.one\")] class OneApp;\n[Neuron(\"app.two\")] class TwoApp;\n[Neuron(\"screen\")] class NotAnApp;");
        sample.Write("Two/Other.cs", "[Neuron(\"app.three\", Alias = \"x\")] class ThreeApp;");
        sample.Write("obj/Generated.cs", "[Neuron(\"app.generated\")] class Skipped;");

        Assert.Equal(3, CountAppNeurons(sample.Root));

        Assert.Null(RatchetFinding(3, 3));
        Assert.Contains("lower", RatchetFinding(2, 3)!, StringComparison.Ordinal);
        Assert.Contains("above", RatchetFinding(4, 3)!, StringComparison.Ordinal);
    }

    private static List<string> LayoutFindings(string root)
    {
        var findings = new List<string>();
        if (!Directory.Exists(root))
        {
            return findings;
        }
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj")
            {
                continue;
            }
            var expected = new[] { $"{name}/IntoChat.{name}.csproj", $"{name}/Contracts/IntoChat.{name}.Contracts.csproj" };
            var present = Repository.SourceFiles(directory, "*.csproj", path => !IsBuildOutput(directory, path))
                .Select(path => name + "/" + Path.GetRelativePath(directory, path).Replace('\\', '/'))
                .ToArray();
            findings.AddRange(expected.Except(present, StringComparer.Ordinal).Select(missing => $"{name}: missing {missing}"));
            findings.AddRange(present.Except(expected, StringComparer.Ordinal).Select(extra => $"{name}: unexpected project {extra}"));
        }
        return findings;
    }

    // Each project directly in <Name>/ or <Name>/Contracts/ carries the PackageId the rule names.
    private static List<string> PackageIdFindings(string root)
    {
        var findings = new List<string>();
        if (!Directory.Exists(root))
        {
            return findings;
        }
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            foreach (var (relative, expected) in new[] { ($"IntoChat.{name}.csproj", $"IntoChat.OS.Components.{name}"), ($"Contracts/IntoChat.{name}.Contracts.csproj", $"IntoChat.OS.Components.{name}.Contracts") })
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    continue;
                }
                var actual = XDocument.Load(path).Descendants().FirstOrDefault(element => element.Name.LocalName == "PackageId")?.Value.Trim();
                if (actual != expected)
                {
                    findings.Add($"{name}/{relative}: PackageId is {actual ?? "unset"}, expected {expected}");
                }
            }
        }
        return findings;
    }

    private static bool IsBuildOutput(string directory, string path)
        => Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin");

    // The projects named under the roots, by project name, so a reference resolves by name even through an MSBuild property.
    private static HashSet<string> ProjectNamesUnder(IEnumerable<string> roots)
        => roots.SelectMany(root => Repository.SourceFiles(root, "*.csproj"))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToHashSet(StringComparer.Ordinal);

    private static List<string> ReferenceFindings(string componentsRoot, IReadOnlyList<string> roots, (string Project, string Referenced)[] allowed, string owed)
    {
        var findings = new List<string>();
        var protectedNames = ProjectNamesUnder(roots);
        var seen = new HashSet<(string, string)>();
        foreach (var project in Repository.SourceFiles(componentsRoot, "*.csproj").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(project)!;
            if (name.EndsWith(".Contracts", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var referenced in Repository.References(project).Where(reference => protectedNames.Contains(reference) && !reference.EndsWith(".Contracts", StringComparison.Ordinal)))
            {
                seen.Add((name, referenced));
                if (!allowed.Contains((name, referenced)))
                {
                    findings.Add($"{name} -> {referenced}: an implementation project of a module or component; reference its Contracts project, or list the pair in AllowedImplementationReferences with an OWED.md line");
                }
            }
        }
        foreach (var (project, referenced) in allowed)
        {
            if (!seen.Contains((project, referenced)))
            {
                findings.Add($"{project} -> {referenced}: stale allowance, the reference no longer occurs; remove it from AllowedImplementationReferences and OWED.md");
            }
            else if (!owed.Split('\n').Any(line => line.Contains(project, StringComparison.Ordinal) && line.Contains(referenced, StringComparison.Ordinal)))
            {
                findings.Add($"{project} -> {referenced}: allowed but no OWED.md line names both projects");
            }
        }
        return findings;
    }

    private static List<string> HostingFindings(string root, string appHostProject)
    {
        var findings = new List<string>();
        if (!Directory.Exists(root))
        {
            return findings;
        }
        var appHost = File.ReadAllText(appHostProject).Replace('\\', '/');
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!Directory.Exists(Path.Combine(directory, "Hosting")))
            {
                continue;
            }
            var project = Path.Combine(directory, $"IntoChat.{name}.csproj");
            var removed = File.Exists(project)
                && XDocument.Load(project).Descendants("Compile")
                    .Select(item => (string?)item.Attribute("Remove") ?? "")
                    .Any(remove => remove.Split(';').Select(part => part.Trim().Replace('\\', '/')).Contains("Hosting/**", StringComparer.Ordinal));
            if (!removed)
            {
                findings.Add($"{name}: IntoChat.{name}.csproj must have <Compile Remove=\"Hosting/**\" />");
            }
            if (!appHost.Contains($"Include=\"../Components/{name}/Hosting/**/*.cs\"", StringComparison.Ordinal))
            {
                findings.Add($"{name}: the AppHost project must have <Compile Include=\"../Components/{name}/Hosting/**/*.cs\" ... />");
            }
        }
        return findings;
    }

    private static List<string> ScreenUsingFindings(string root)
    {
        var findings = new List<string>();
        if (!Directory.Exists(root))
        {
            return findings;
        }
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var component = Path.GetFileName(directory);
            var screens = Path.Combine(directory, "Screens");
            foreach (var file in Repository.SourceFiles(screens, "*.cs").Order(StringComparer.Ordinal))
            {
                foreach (Match match in UsingDirective().Matches(File.ReadAllText(file)))
                {
                    var target = match.Groups["target"].Value;
                    if (!ScreenMayUse(component, target))
                    {
                        findings.Add($"{DisplayPath(root, file)}: using {target};");
                    }
                }
            }
        }
        return findings;
    }

    private static bool ScreenMayUse(string component, string ns)
        => IsOrBelow(ns, "IntoChat." + component)
            || IsOrBelow(ns, "IntoChat.OS.UI")
            || ns == "IntoChat.OS"
            || IsOrBelow(ns, "IntoChat.OS.Host")
            || IsOrBelow(ns, "IntoChat.OS.Modules.Apps")
            || IsOrBelow(ns, "DigitalBrain")
            || IsOrBelow(ns, "System")
            || IsOrBelow(ns, "Microsoft.Extensions");

    private static bool IsOrBelow(string ns, string root)
        => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    private static string DisplayPath(string root, string file)
        => Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');

    private static int CountAppNeurons(string directory)
        => Repository.SourceFiles(directory, "*.cs").Sum(path => AppNeuron().Count(File.ReadAllText(path)));

    private static string? RatchetFinding(int count, int cap)
        => count > cap
            ? $"[Neuron(\"app.…\")] declarations in IntoChat.OS.Apps are {count}, above the cap {cap}; a new app is a component's Screens, not an Apps class"
            : count < cap
                ? $"[Neuron(\"app.…\")] declarations in IntoChat.OS.Apps are {count}, below the cap {cap}; lower AppsLeftCap to {count} so the number only goes down"
                : null;

    private static string References(params string[] includes)
        => "<Project><ItemGroup>" + string.Concat(includes.Select(include => $"<ProjectReference Include=\"{include}\" />")) + "</ItemGroup></Project>";

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?<target>[\w.]+)\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"\[Neuron\(""app\.", RegexOptions.CultureInvariant)]
    private static partial Regex AppNeuron();

    private sealed class Sample : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("components-sample-").FullName;

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
