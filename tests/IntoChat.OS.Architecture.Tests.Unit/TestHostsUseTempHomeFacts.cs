namespace IntoChat.OS.Architecture.Tests.Unit;

// A test host keeps its state under a temporary IntoChatOS:Home (TestHome), never under the developer's ~/.intochat:
// the shared hosting bases create one per brain, and no test or test-support source points Home at the user profile.
public sealed class TestHostsUseTempHomeFacts
{
    private static readonly string[] HomeSpellings = ["IntoChatOSNames.HomeKey", "IntoChatOS:Home", "IntoChatOS__Home"];
    private static readonly string[] ProfileSpellings = ["SpecialFolder.UserProfile", "StateHome.Default(", "\"~/.intochat", "\".intochat\""];

    private static IEnumerable<Repository.Project> TestAndTestingProjects()
        => Repository.Projects.Values.Where(project => project.Name.Contains(".Tests", StringComparison.Ordinal)
            || project.Name.Contains(".Testing", StringComparison.Ordinal));

    [Fact]
    public void NoTestSourcePointsHomeAtTheUserProfile()
    {
        var problems = new List<string>();
        foreach (var project in TestAndTestingProjects())
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs", path =>
                !path.EndsWith("StateHomeFacts.cs", StringComparison.Ordinal) && !path.EndsWith("TestHostsUseTempHomeFacts.cs", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                if (HomeSpellings.Any(spelling => text.Contains(spelling, StringComparison.Ordinal))
                    && ProfileSpellings.Any(spelling => text.Contains(spelling, StringComparison.Ordinal)))
                { problems.Add(Repository.Relative(file)); }
            }
        }
        Assert.True(problems.Count == 0, "Test hosts take a TestHome, not the user profile:\n" + string.Join('\n', problems));
    }
}
