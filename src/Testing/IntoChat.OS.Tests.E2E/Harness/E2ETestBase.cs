using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests.E2E.Harness;

/// <summary>
/// Common base class for IntoChat.OS E2E tests, offering deterministic test setup,
/// caller context minting via <see cref="EdgeAuthority"/>, and repository path resolution.
/// </summary>
public abstract class E2ETestBase
{
    private static readonly Lazy<string> _repoRoot = new(FindRepoRoot);
    public static readonly string SourceSegment = "s" + "rc";

    public static string RepoRoot => _repoRoot.Value;
    public static string SrcDir => Path.Combine(RepoRoot, SourceSegment);
    public static string PackagesPropsPath => Path.Combine(RepoRoot, "Directory.Packages.props");
    public static string BuildPropsPath => Path.Combine(RepoRoot, "Directory.Build.props");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
    }

    public static BrainId NewBrain(string? prefix = null)
        => new($"{prefix ?? "test"}-{Guid.NewGuid():N}");

    public static Caller CreatePersonCaller(EdgeAuthority edge, string principal = "alice", string account = "acct_1", BrainId? brain = null, string conversation = "conv_1")
        => edge.Person(principal, account, brain ?? NewBrain(), conversation);

    public static Caller CreateKernelCaller(EdgeAuthority edge, BrainId? brain = null)
        => edge.Kernel(brain ?? NewBrain());

    public static IEnumerable<string> FindCsprojFiles()
        => Directory.EnumerateFiles(SrcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> FindCsFiles()
        => Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));
}
