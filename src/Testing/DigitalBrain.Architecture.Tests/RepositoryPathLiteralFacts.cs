using System.Text.RegularExpressions;

namespace DigitalBrain.Architecture.Tests;

// A repository path written into a test is a fact about the directory tree that nothing verifies
// until the tree moves. Tests resolve files through the solution instead (Repository here, a
// project-name lookup in DigitalBrain.slnx elsewhere); a workflow may name a path, but the path
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
            $"Tests resolve repository files through {Repository.SolutionFile} (Repository in DigitalBrain.Architecture.Tests, "
            + "or a project-name lookup of the solution), never through a \"" + SourceRootName + "/…\" literal:\n"
            + string.Join('\n', offenders));
    }

    // A fact names what it wants through the typed harness; the hosting ring decides the
    // variable. Raw OTEL_/ASPNETCORE_/DOTNET_ names and "A__B" env spellings are forbidden in test
    // code outside the harness itself, as is spelling an env name through EnvironmentName(...).
    [GeneratedRegex("\"(OTEL_|ASPNETCORE_|DOTNET_|ASPIRE_)[A-Z0-9_]+\"|\"[A-Za-z0-9]+__[A-Za-z0-9_]+\"|EnvironmentName\\(", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentLiteral();

    // Closed list: these facts assert on the projection itself (which variables the hosting and
    // deployment rings write), so the variable names are their subject, not a way to configure a run.
    private static readonly string[] ProjectionFacts =
    [
        "TelemetryProjectionFacts.cs", "HostEnvironmentFacts.cs", "ConfigurationFileFacts.cs", "ConfigurationGrammarFacts.cs",
        "DeploymentIdentityFacts.cs", "DigitalBrainDeploymentFacts.cs", "GitHubAppHostingFacts.cs", "PostgresAspireHostingFacts.cs",
        "SupabaseHostingFacts.cs", "ProductCompositionFacts.cs"
    ];

    [Fact]
    public void TestsNameNoEnvironmentVariables()
    {
        var offenders = Repository.ProjectFiles("*.cs",
                path => EnvironmentLiteral().IsMatch(File.ReadAllText(path)),
                project => project.Name.Contains(".Tests", StringComparison.Ordinal)
                    && !project.Directory.Contains(Path.DirectorySeparatorChar + "Testing" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => !ProjectionFacts.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Select(Repository.Relative)
            .Order().ToArray();
        Assert.True(offenders.Length == 0,
            "Test code names environment variables; speak through the E2E builder (ExportingTelemetryTo, InEnvironment, ConfigureModule, WithHostConfiguration with a typed key):\n"
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
                if (!File.Exists(path) && !Directory.Exists(path)) { missing.Add(Path.GetFileName(workflow) + ": " + match.Value); }
            }
        }
        Assert.True(missing.Count == 0, "Workflow paths that no longer exist:\n" + string.Join('\n', missing.Distinct().Order()));
    }
}
