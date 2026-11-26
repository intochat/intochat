using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A repository path written into a test is a fact about the directory tree that nothing verifies
// until the tree moves. Tests resolve files through the solution instead (Repository here, a
// project-name lookup in Intochat.OS.slnx elsewhere); a workflow may name a path, but the path
// must exist. The source-root name is spelled by concatenation so this file never matches itself.
public sealed partial class RepositoryPathLiteralFacts
{
    private static readonly string SourceRootName = "s" + "rc";

    // A string literal that starts a repository path, or a bare source-root segment handed to Path.Combine.
    [GeneratedRegex("\"src(/|\")", RegexOptions.CultureInvariant)]
    private static partial Regex SourceLiteral();

    [GeneratedRegex(@"(?<![A-Za-z0-9_./-])src/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowPath();

    [Fact]
    public void TestsCarryNoRepositoryPathLiterals()
    {
        var offenders = Repository.ProjectFiles("*.cs",
                path => SourceLiteral().IsMatch(File.ReadAllText(path)),
                project => project.Name.Contains(".Tests", StringComparison.Ordinal))
            .Select(Repository.ProjectKey).Order().ToArray();
        Assert.True(offenders.Length == 0,
            $"Tests resolve repository files through {Repository.SolutionFile} (Repository in IntoChat.OS.Architecture.Tests.Unit, "
            + "or a project-name lookup of the solution), never through a \"" + SourceRootName + "/…\" literal:\n"
            + string.Join('\n', offenders));
    }

    // A fact names what it wants through the typed harness; the hosting ring decides the
    // variable. Raw OTEL_/ASPNETCORE_/DOTNET_ names and "A__B" env spellings are forbidden in test
    // code outside the harness itself, as is spelling an env name through EnvironmentName(...).
    [GeneratedRegex("\\$?\"(OTEL_|ASPNETCORE_|DOTNET_|ASPIRE_)|\"[A-Za-z0-9]+__[A-Za-z0-9_]+\"|\"__[A-Za-z0-9\"]|[A-Za-z0-9}]__\"|\\}__[A-Za-z0-9]|EnvironmentName\\(", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentLiteral();

    // Closed list, keyed <Project>/<path inside it>: these facts assert on the projection itself
    // (which variables the hosting and deployment rings write), so the variable names are their
    // subject, not a way to configure a run.
    private static readonly string[] ProjectionFacts =
    [
        "IntoChat.OS.Tests.Unit/AspireHosting/HostEnvironmentFacts.cs",
        "IntoChat.OS.Tests.Unit/AspireHosting/ConfigurationFileFacts.cs",
        "IntoChat.OS.Tests.Unit/AspireHosting/DevicesHostingFacts.cs",
        "IntoChat.OS.Tests.Unit/AspireHosting/AppHostChainFacts.cs",
        "IntoChat.OS.Tests.Unit/AspireHosting/WebModuleHostingFacts.cs",
        "IntoChat.OS.Tests.E2E/Aspire/BootFacts.cs",
        "IntoChat.OS.Tests.Unit/Components/Aspire/ProcessCandidateHostFacts.cs",
        "IntoChat.OS.Tests.Integration/Components/Aspire/ProcessCandidateHostProcessFacts.cs",
        "IntoChat.OS.Tests.Unit/Modules/Plugins/GitHub/LocalCheckoutRegistrationFacts.cs",
    ];

    [Fact]
    public void ProjectionFactsExist()
    {
        var missing = ProjectionFacts.Where(key => !File.Exists(Repository.FromProjectKey(key))).ToArray();
        Assert.True(missing.Length == 0, "Exempted projection facts that no longer exist:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void TestsNameNoEnvironmentVariables()
    {
        var offenders = Repository.ProjectFiles("*.cs",
                path => EnvironmentLiteral().IsMatch(File.ReadAllText(path)),
                project => project.Name.Contains(".Tests", StringComparison.Ordinal)
                    && project.Name is not ("IntoChat.OS.Tests.Support" or "IntoChat.OS.Architecture.Tests.Unit"))
            .Select(Repository.ProjectKey)
            .Where(key => !ProjectionFacts.Contains(key, StringComparer.Ordinal))
            .Order().ToArray();
        Assert.True(offenders.Length == 0,
            "Test code names environment variables; speak through TestBrain (WithIntegration, WithOptions, WithDashboardTelemetry, InEnvironment, WithHostConfiguration with a typed key):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void WorkflowPathsExist()
    {
        var workflows = Path.Combine(Repository.Root, ".github", "workflows");
        var missing = new List<string>();
        foreach (var workflow in Directory.EnumerateFiles(workflows, "*.yml"))
        {
            foreach (Match match in WorkflowPath().Matches(File.ReadAllText(workflow)))
            {
                // Build output under a checked-in directory is not on disk until a build runs.
                var segments = match.Value.Split('/').TakeWhile(segment => segment is not ("bin" or "obj" or "build")).ToArray();
                var path = Path.Combine([Repository.Root, .. segments]);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    missing.Add(Path.GetFileName(workflow) + ": " + match.Value);
                }
            }
        }
        Assert.True(missing.Count == 0, "Workflow paths that no longer exist:\n" + string.Join('\n', missing.Distinct().Order()));
    }
}
