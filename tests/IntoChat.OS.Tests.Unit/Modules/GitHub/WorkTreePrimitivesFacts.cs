using System.Reflection;
using System.Text.Json;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit;

public sealed class WorkTreePrimitivesFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(typeof(RepositoryId))]
    [InlineData(typeof(BranchName))]
    [InlineData(typeof(CommitSha))]
    [InlineData(typeof(RepoPath))]
    [InlineData(typeof(ChangeSet))]
    public void ValueObjectsAreNonDefaultablePrivateCtorAndHaveNoImplicitStringOperator(Type type)
    {
        Assert.False(type.IsValueType, $"{type.Name} must be a non-defaultable reference type.");
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var implicitToString = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit" && m.ReturnType == typeof(string));
        Assert.Empty(implicitToString);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("intochat/os")]
    [InlineData("my-org/repo_1.git")]
    public void RepositoryIdParsesAndRoundTripsJson(string raw)
    {
        var id = RepositoryId.Parse(raw);
        Assert.Equal(raw, id.Value);

        var roundTripped = JsonSerializer.Deserialize<RepositoryId>(JsonSerializer.Serialize(id, Json), Json);
        Assert.Equal(id, roundTripped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("intochat/os\nRemote: https://evil.example/x.git")]
    [InlineData("../escape")]
    [InlineData("foo\\bar")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("a//b")]
    public void RepositoryIdRejectsInvalidOrInjectedValues(string bad)
    {
        Assert.False(RepositoryId.TryParse(bad, out _));
        Assert.Throws<ArgumentException>(() => RepositoryId.Parse(bad));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("ino/fix-typo-1234")]
    [InlineData("refactor/ddd-clean-os")]
    public void BranchNameParsesAndBuildsRemoteRef(string raw)
    {
        var branch = BranchName.Parse(raw);
        Assert.Equal(raw, branch.Value);
        Assert.Equal($"refs/heads/{raw}", branch.RemoteRef);

        var roundTripped = JsonSerializer.Deserialize<BranchName>(JsonSerializer.Serialize(branch, Json), Json);
        Assert.Equal(branch, roundTripped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("a..b")]
    [InlineData("a@{b}")]
    [InlineData("bad branch")]
    [InlineData("bad\nbranch")]
    [InlineData("branch.lock")]
    [InlineData("a//b")]
    public void BranchNameRejectsInvalidGitRefNames(string bad)
    {
        Assert.False(BranchName.TryParse(bad, out _));
        Assert.Throws<ArgumentException>(() => BranchName.Parse(bad));
    }

    [Fact]
    public void BranchNameFromRequestedBaseStripsOriginPrefix()
    {
        Assert.Equal(BranchName.Parse("main"), BranchName.FromRequestedBase("origin/main"));
        var feature = BranchName.CreateFeatureBranch("Add Multiply helper!", "run99887766");
        Assert.True(feature.IsFeatureBranch);
        Assert.StartsWith("ino/add-multiply-helper-run99887", feature.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitShaValidatesExact40HexAndNormalizesLowercase()
    {
        var upper = new string('A', 40);
        var sha = CommitSha.Parse(upper);
        Assert.Equal(new string('a', 40), sha.Value);
        Assert.Equal(new string('a', 8), sha.Short);

        Assert.Throws<ArgumentException>(() => CommitSha.Parse("1234"));
        Assert.Throws<ArgumentException>(() => CommitSha.Parse(new string('g', 40)));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\temp\\a.cs")]
    [InlineData("./a.cs")]
    [InlineData("a//b.cs")]
    [InlineData("a/./b.cs")]
    [InlineData("a\0b.cs")]
    [InlineData("")]
    public void RepoPathRejectsTraversalRootedAndMalformedPaths(string bad)
    {
        Assert.False(RepoPath.TryParse(bad, out _));
        Assert.Throws<ArgumentException>(() => RepoPath.Parse(bad));
    }

    [Fact]
    public void ChangeSetDeduplicatesSortsAndCountsDiffLines()
    {
        var sha = CommitSha.Parse(new string('1', 40));
        var diff = "--- a/code/B.cs\n+++ b/code/B.cs\n@@ -1,2 +1,2 @@\n-old\n+new1\n+new2\n";
        var set = ChangeSet.FromDiff(
            sha,
            [RepoPath.Parse("code\\B.cs"), RepoPath.Parse("code/A.cs"), RepoPath.Parse("code/B.cs")],
            diff);

        Assert.Equal([RepoPath.Parse("code/A.cs"), RepoPath.Parse("code/B.cs")], set.ChangedFiles);
        Assert.Equal(2, set.Additions);
        Assert.Equal(1, set.Deletions);

        var roundTripped = JsonSerializer.Deserialize<ChangeSet>(JsonSerializer.Serialize(set, Json), Json);
        Assert.NotNull(roundTripped);
        Assert.Equal(set.Commit, roundTripped.Commit);
        Assert.Equal(set.ChangedFiles, roundTripped.ChangedFiles);
    }
}
