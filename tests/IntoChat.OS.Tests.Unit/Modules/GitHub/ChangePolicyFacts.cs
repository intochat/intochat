using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class ChangePolicyFacts
{
    [Theory]
    [InlineData("code/Greeter.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("ui/lib/app.dart", true)]
    [InlineData("notes/todo.txt", true)]
    [InlineData("views/Main.xaml", true)]
    [InlineData("assets/logo.svg", true)]
    [InlineData(".github/workflows/pwn.yml", false)]
    [InlineData(".gitignore", false)]
    [InlineData(".claude/settings.json", false)]
    [InlineData(".git/config", false)]
    [InlineData("CLAUDE.md", false)]
    [InlineData("code/AGENTS.md", false)]
    [InlineData("apps/SKILL.md", false)]
    [InlineData(".hidden/Foo.cs", false)]
    [InlineData("docs/copilot-instructions.md", false)]
    [InlineData("scripts/run.ps1", false)]
    [InlineData("bin/tool.exe", false)]
    [InlineData("Makefile", false)]
    public void CheckEvaluatesAllowedExtensionsBlockedInstructionFilesAndDotfiles(string relativePath, bool allowed)
    {
        var policy = ChangePolicy.Default;
        var path = RepoPath.Parse(relativePath);

        var refusal = policy.Check(path);

        if (allowed)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.NotNull(refusal);
        }
    }

    [Fact]
    public void CheckRefusesEmptyAndOverBudgetChangeListsWithoutGit()
    {
        var policy = ChangePolicy.Default;

        Assert.NotNull(policy.Check(Array.Empty<RepoPath>()));

        var five = Enumerable.Range(1, 5).Select(i => RepoPath.Parse($"code/File{i}.cs")).ToArray();
        Assert.Null(policy.Check(five));

        var six = Enumerable.Range(1, 6).Select(i => RepoPath.Parse($"code/File{i}.cs")).ToArray();
        var refusal = policy.Check(six);
        Assert.NotNull(refusal);
        Assert.Contains("5", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRendersPolicyConstraintsForPromptConsumption()
    {
        var custom = new ChangePolicy(
            maxChangedFiles: 3,
            allowedExtensions: [".cs", ".md"],
            blockedFileNames: ["AGENTS.md", "CLAUDE.md"]);

        var prompt = custom.Describe();

        Assert.Contains("3", prompt, StringComparison.Ordinal);
        Assert.Contains(".cs", prompt, StringComparison.Ordinal);
        Assert.Contains(".md", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(".dart", prompt, StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CLAUDE.md", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fix null check [skip ci]", "Fix null check")]
    [InlineData("Fix null check [CI SKIP] please", "Fix null check please")]
    [InlineData("Fix\0 control\u202E [no ci]\n[skip actions] line", "Fix control\nline")]
    [InlineData("Change [actions skip]\nskip-checks: true\nDone", "Change\nDone")]
    public void SanitizeCommitTextStripsSkipCiTokensAndControlCharacters(string raw, string expected)
    {
        var cleaned = ChangePolicy.SanitizeCommitText(raw);
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void BuildCommitMessageAndPrTitleStripSkipCiTokensFromGoalAndSummary()
    {
        var message = ChangePolicy.BuildCommitMessage("Add helper [skip ci]", "Clean summary [ci skip]\nskip-checks: true");

        Assert.DoesNotContain("[skip ci]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[ci skip]", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skip-checks", message, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("feat(ino): Add helper", message, StringComparison.Ordinal);
        Assert.Contains("Clean summary", message, StringComparison.Ordinal);

        var title = ChangePolicy.BuildPrTitle("Add helper [no ci]\nsecond line");
        Assert.Equal("Add helper", title);
    }

    [Fact]
    public void ForRepositoryOs_AllowsEightFilesAndCsprojJson_RefusesDotDirsAndClaudeMd()
    {
        var policy = ChangePolicy.ForRepository("os");

        Assert.Equal(8, policy.MaxChangedFiles);
        Assert.Null(policy.Check(RepoPath.Parse("src/Modules/Web/IntoChat.OS.Modules.Web.csproj")));
        Assert.Null(policy.Check(RepoPath.Parse("src/Assets/wire/component.json")));
        Assert.Null(policy.Check(RepoPath.Parse("src/Modules/Web/Foo.cs")));

        var eight = new[]
        {
            RepoPath.Parse("src/Modules/Web/IntoChat.OS.Modules.Web.csproj"),
            RepoPath.Parse("src/Assets/wire/component.json"),
            RepoPath.Parse("src/Modules/Web/File1.cs"),
            RepoPath.Parse("src/Modules/Web/File2.cs"),
            RepoPath.Parse("src/Modules/Web/File3.cs"),
            RepoPath.Parse("src/Modules/Web/File4.cs"),
            RepoPath.Parse("src/Modules/Web/File5.cs"),
            RepoPath.Parse("src/Modules/Web/File6.cs"),
        };
        Assert.Null(policy.Check(eight));

        var nine = eight.Append(RepoPath.Parse("src/Modules/Web/File7.cs")).ToArray();
        var budgetRefusal = policy.Check(nine);
        Assert.NotNull(budgetRefusal);
        Assert.Contains("8", budgetRefusal, StringComparison.Ordinal);

        Assert.NotNull(policy.Check(RepoPath.Parse(".github/workflows/ci.yml")));
        Assert.NotNull(policy.Check(RepoPath.Parse("CLAUDE.md")));

        Assert.Equal(ChangePolicy.Default, ChangePolicy.ForRepository("other-repo"));
    }
}
