using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Workflow guard rails that no run can prove while Actions is unavailable: nightly e2e never gates a PR or push,
// the timing report never blocks ci, and ArtCraft images come from GHCR instead of being built in e2e.
public sealed partial class CiWorkflowFacts
{
    private static string Workflow(string name) => File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", name));

    // The text of one top-level job: from "  <name>:" to the next two-space-indented key.
    private static string Job(string workflow, string name)
    {
        var match = Regex.Match(workflow, $@"^  {Regex.Escape(name)}:\s*$(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"job '{name}' not found");
        return match.Groups["body"].Value;
    }

    [GeneratedRegex(@"^\s*(push|pull_request|pull_request_target)\s*:", RegexOptions.Multiline)]
    private static partial Regex GatingTrigger();

    [Fact]
    public void E2EIsNightlyOrDispatchedOnly()
    {
        var workflow = Workflow("e2e.yml");
        var on = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];
        Assert.Contains("schedule:", on, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", on, StringComparison.Ordinal);
        Assert.DoesNotMatch(GatingTrigger(), on);
    }

    [Fact]
    public void E2EDoesNotBuildArtCraftFromSource()
    {
        var workflow = Workflow("e2e.yml");
        Assert.DoesNotContain("craft-mcp.Dockerfile", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("craft-web.Dockerfile", workflow, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/intochat/os-artcraft", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportJobNeverBlocksCi()
    {
        var workflow = Workflow("ci.yml");
        Assert.Matches(@"(?m)^\s*continue-on-error:\s*true\s*$", Job(workflow, "report"));
        var needs = Regex.Match(Job(workflow, "ci"), @"(?m)^\s*needs:\s*\[(?<list>[^\]]*)\]");
        Assert.True(needs.Success, "ci job must list its needs inline");
        Assert.DoesNotContain("report", needs.Groups["list"].Value.Split(',').Select(n => n.Trim()));
    }

    [Fact]
    public void OnlyTheWarmWorkflowMayWritePackages()
    {
        Assert.Contains("packages: write", Workflow("artcraft-images.yml"), StringComparison.Ordinal);
        var e2e = Workflow("e2e.yml");
        Assert.Contains("packages: read", e2e, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", e2e, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarmWorkflowUsesOnlyTheRepositoryToken()
    {
        var secrets = Regex.Matches(Workflow("artcraft-images.yml"), @"secrets\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct();
        Assert.All(secrets, secret => Assert.Equal("GITHUB_TOKEN", secret));
    }
}
