using System.Security;
using System.Text.Json;
using DigitalBrain.Confirmation;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// The verification policy of an engineering run as the run aggregate holds it: every gate on by default, a skipped gate only
// when the policy says so, and never a skip that the run does not record.
public sealed class EngineerVerificationPolicyFacts
{
    private static readonly CommitSha Sha = CommitSha.Parse(new string('d', 40));

    private static readonly WorkTreeLease Lease = new("r1", "os", "/repo", "/wt/0", "/repo/.git/wt/0", new string('c', 64), "main", new string('0', 40), "ino/feat-r1");

    private static readonly EngineerBuildReport BuildOk = new(true, true, true, true, 5, 0, 0, 0, [], 100, "OK");

    private static readonly EngineerLiveReport LiveOk = new(true, true, true, true, 0, 0, 0, 0, [], [], "OK");

    private static readonly ConfirmationResult PersonApproved =
        new("p", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false) { Approver = ConfirmationPrincipal.Person("person-1") };

    private static EngineerRun Committed(EngineerVerificationPolicy? policy, EngineerBuildReport? build, EngineerLiveReport? live)
    {
        var plan = new EngineerTaskPlan("Goal", "Arch", "Critic", [], [new("t1", "Task", "csharp", ["code/Calculator.cs"], [], "Do")]);
        var commit = ChangeSet.FromDiff(Sha, [RepoPath.Parse("code/Calculator.cs")], "--- a/code/Calculator.cs\n+++ b/code/Calculator.cs\n@@ -1 +1 @@\n-a\n+b\n");
        return EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal", verification: policy)
            .WithPlan(plan)
            .WithLeases("r1", [Lease])
            .WithUpdatedInstances([new EngineerInstance(0, Lease, ["t1"], new CoderEdit("Done", "m", [RepoPath.Parse("code/Calculator.cs")]), [], commit, build, live, "implemented")], 0);
    }

    [Fact]
    public void Every_gate_is_on_unless_the_run_was_started_otherwise()
    {
        var run = EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal");

        Assert.Equal(EngineerVerificationPolicy.Full, run.Verification);
        Assert.True(run.Verification.BuildAndTest);
        Assert.True(run.Verification.LiveVerify);
        Assert.False(run.Verification.SkipsAny);
        Assert.Empty(run.Verification.SkippedGates);
        Assert.Null(run.Verification.SkippedBy);
    }

    [Fact]
    public void A_run_stored_before_the_policy_existed_passes_every_gate()
    {
        const string stored = """{"runId":"old1","repository":"os","goal":"Goal","instanceCount":1,"instances":[],"selectedInstanceIndex":0}""";

        var run = JsonSerializer.Deserialize<EngineerRun>(stored, EngineerFlow.Json)!;

        Assert.Equal(EngineerVerificationPolicy.Full, run.Verification);
    }

    [Fact]
    public void The_policy_is_kept_with_the_run()
    {
        var run = EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal", verification: new EngineerVerificationPolicy(BuildAndTest: true, LiveVerify: false, SkippedBy: "ada"));

        var json = JsonSerializer.Serialize(run, EngineerFlow.Json);
        var read = JsonSerializer.Deserialize<EngineerRun>(json, EngineerFlow.Json)!;

        Assert.Equal(new EngineerVerificationPolicy(true, false, "ada"), read.Verification);
        Assert.Equal(["live-verify"], read.Verification.SkippedGates);
        Assert.Contains("\"verification\":{\"buildAndTest\":true,\"liveVerify\":false,\"skippedBy\":\"ada\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_payload_chooses_the_gates_but_cannot_say_who_skipped_them()
    {
        const string payload = """{"runId":"r1","repository":"os","goal":"Goal","verification":{"buildAndTest":false,"liveVerify":false,"skippedBy":"the-owner-honest"}}""";

        var run = EngineerRun.FromUntrustedJson(payload, EngineerFlow.Json);

        Assert.False(run.Verification.BuildAndTest);
        Assert.False(run.Verification.LiveVerify);
        Assert.Null(run.Verification.SkippedBy);
        Assert.Equal("ada", run.WithVerificationSkippedBy("ada").Verification.SkippedBy);
        Assert.Null(EngineerRun.FromUntrustedJson("""{"repository":"os","goal":"Goal"}""", EngineerFlow.Json).WithVerificationSkippedBy("ada").Verification.SkippedBy);
    }

    [Fact]
    public void With_every_gate_on_a_commit_reaches_approval_only_after_build_and_live_passed()
    {
        Assert.Throws<InvalidOperationException>(() => Committed(null, null, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => Committed(null, BuildOk, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));

        var ready = Committed(null, BuildOk, LiveOk).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("person-1", ready.EnsureCanPublish(PersonApproved, Sha));
    }

    [Fact]
    public void A_skipped_gate_is_not_asked_for_but_a_gate_left_on_still_is()
    {
        var liveOff = new EngineerVerificationPolicy(LiveVerify: false, SkippedBy: "ada");
        Assert.Throws<InvalidOperationException>(() => Committed(liveOff, null, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));
        var builtOnly = Committed(liveOff, BuildOk, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("person-1", builtOnly.EnsureCanPublish(PersonApproved, Sha));

        var bothOff = new EngineerVerificationPolicy(BuildAndTest: false, LiveVerify: false, SkippedBy: "ada");
        var unchecked_ = Committed(bothOff, null, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("person-1", unchecked_.EnsureCanPublish(PersonApproved, Sha));
    }

    [Fact]
    public void A_skip_never_weakens_the_person_approval()
    {
        var bothOff = new EngineerVerificationPolicy(BuildAndTest: false, LiveVerify: false, SkippedBy: "ada");
        var run = Committed(bothOff, null, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1));
        var agentApproved = new ConfirmationResult("p", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false) { Approver = ConfirmationPrincipal.Agent("ino") };

        Assert.Throws<SecurityException>(() => run.EnsureCanPublish(agentApproved, Sha));
        Assert.Throws<SecurityException>(() => run.EnsureCanPublish(null, Sha));
    }

    [Fact]
    public void A_gate_that_ran_and_failed_is_never_treated_as_skipped()
    {
        var failedBuild = BuildOk with { Succeeded = false, Summary = "1 test failed" };
        var buildOff = new EngineerVerificationPolicy(BuildAndTest: false, SkippedBy: "ada");

        // The policy skips the gate; it does not forgive a result that is there.
        Assert.Throws<InvalidOperationException>(() => Committed(buildOff, failedBuild, LiveOk).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public void The_policy_says_in_one_line_what_was_skipped_and_by_whom()
    {
        Assert.Equal("Verification: build-and-test and live-verify are on.", EngineerVerificationPolicy.Full.Describe());
        Assert.Equal(
            "Verification SKIPPED for live-verify by ada: this commit was not checked by that gate.",
            new EngineerVerificationPolicy(LiveVerify: false, SkippedBy: "ada").Describe());
        Assert.Equal(
            "Verification SKIPPED for build-and-test and live-verify by ada: this commit was not checked by those gates.",
            new EngineerVerificationPolicy(false, false, "ada").Describe());
    }

    [Fact]
    public void A_run_stored_before_not_available_existed_still_reads()
    {
        // The policy as it was stored before the outcome existed: three members.
        const string stored = """{"runId":"old2","repository":"os","goal":"Goal","instanceCount":1,"instances":[],"selectedInstanceIndex":0,"verification":{"buildAndTest":true,"liveVerify":false,"skippedBy":"ada"}}""";

        var run = JsonSerializer.Deserialize<EngineerRun>(stored, EngineerFlow.Json)!;

        Assert.Equal(new EngineerVerificationPolicy(true, false, "ada"), run.Verification);
        Assert.False(run.Verification.LiveVerifyNotAvailable);
        // And it is written back as it was: the new member is written only when it is set.
        Assert.Contains("\"verification\":{\"buildAndTest\":true,\"liveVerify\":false,\"skippedBy\":\"ada\"}", JsonSerializer.Serialize(run, EngineerFlow.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void Not_available_is_kept_with_the_run_and_is_neither_a_pass_nor_a_skip()
    {
        var run = EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal").WithLiveVerifyNotAvailable();

        var json = JsonSerializer.Serialize(run, EngineerFlow.Json);
        var read = JsonSerializer.Deserialize<EngineerRun>(json, EngineerFlow.Json)!;

        Assert.Contains("\"verification\":{\"buildAndTest\":true,\"liveVerify\":true,\"skippedBy\":null,\"liveVerifyNotAvailable\":true}", json, StringComparison.Ordinal);
        Assert.True(read.Verification.LiveVerifyNotAvailable);
        Assert.True(read.Verification.LiveVerify);
        Assert.False(read.Verification.SkipsAny);
        Assert.Empty(read.Verification.SkippedGates);
        Assert.Null(read.Verification.SkippedBy);
        Assert.True(read.Verification.LeavesUnchecked);
        Assert.False(EngineerVerificationPolicy.Full.LeavesUnchecked);
    }

    [Fact]
    public void A_start_payload_cannot_say_that_live_verify_is_not_available()
    {
        const string payload = """{"runId":"r1","repository":"os","goal":"Goal","verification":{"buildAndTest":true,"liveVerify":true,"liveVerifyNotAvailable":true}}""";

        var run = EngineerRun.FromUntrustedJson(payload, EngineerFlow.Json);

        Assert.Equal(EngineerVerificationPolicy.Full, run.Verification);
        // A person's skip is the other path: a run whose live-verify was switched off records no "not available".
        var skipped = EngineerRun.Start("r1", RepositoryId.Parse("os"), "Goal", verification: new EngineerVerificationPolicy(LiveVerify: false, SkippedBy: "ada"));
        Assert.False(skipped.WithLiveVerifyNotAvailable().Verification.LiveVerifyNotAvailable);
    }

    [Fact]
    public void Not_available_lets_a_built_commit_reach_approval_but_forgives_no_failure()
    {
        var notAvailable = new EngineerVerificationPolicy(LiveVerifyNotAvailable: true);
        var liveFailed = LiveOk with { Succeeded = false, Summary = "MCP probe failed" };

        // Build-and-test still gates.
        Assert.Throws<InvalidOperationException>(() => Committed(notAvailable, null, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => Committed(notAvailable, BuildOk with { Succeeded = false }, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));
        // A live check that ran and failed is never forgiven by the outcome.
        Assert.Throws<InvalidOperationException>(() => Committed(notAvailable, BuildOk, liveFailed).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1)));

        var ready = Committed(notAvailable, BuildOk, null).WithApprovalPending("app:engineer:r1:" + Sha.Value, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal("person-1", ready.EnsureCanPublish(PersonApproved, Sha));
        // The person approval is what it was.
        Assert.Throws<SecurityException>(() => ready.EnsureCanPublish(null, Sha));
    }

    [Fact]
    public void The_policy_says_plainly_that_a_change_was_not_live_verified()
    {
        const string line = "NOT LIVE-VERIFIED: no live environment is available on this installation, so live-verify did not run and this commit was not checked against a running system.";

        Assert.Equal(line, new EngineerVerificationPolicy(LiveVerifyNotAvailable: true).Describe());
        Assert.Equal(
            "Verification SKIPPED for build-and-test by ada: this commit was not checked by that gate. " + line,
            new EngineerVerificationPolicy(BuildAndTest: false, SkippedBy: "ada", LiveVerifyNotAvailable: true).Describe());
    }

    [Fact]
    public void The_pull_request_says_that_the_change_was_not_live_verified()
    {
        var run = Committed(null, BuildOk, null).WithLiveVerifyNotAvailable();
        var winner = run.SelectedInstance!;

        var body = EngineerPipelineStages.BuildPullRequestBody("Goal", "Done", "m", winner.Commit!, "person-1", "plan-1", new EngineerPullRequestContext(run.Plan, 1, winner, null, run.Verification));
        var full = EngineerPipelineStages.BuildPullRequestBody("Goal", "Done", "m", winner.Commit!, "person-1", "plan-1", new EngineerPullRequestContext(run.Plan, 1, winner, null, EngineerVerificationPolicy.Full));

        Assert.Contains("- **Not live-verified:** `live-verify` did not run because no live environment is available", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Verification skipped", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Not live-verified", full, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pull_request_names_a_gate_that_was_switched_off()
    {
        var policy = new EngineerVerificationPolicy(BuildAndTest: true, LiveVerify: false, SkippedBy: "ada");
        var run = Committed(policy, BuildOk, null);
        var winner = run.SelectedInstance!;

        var skipped = EngineerPipelineStages.BuildPullRequestBody("Goal", "Done", "m", winner.Commit!, "person-1", "plan-1", new EngineerPullRequestContext(run.Plan, 1, winner, null, run.Verification));
        var full = EngineerPipelineStages.BuildPullRequestBody("Goal", "Done", "m", winner.Commit!, "person-1", "plan-1", new EngineerPullRequestContext(run.Plan, 1, winner, null, EngineerVerificationPolicy.Full));

        Assert.Contains("- **Verification skipped:** `live-verify` (switched off by `ada` when the run was started)", skipped, StringComparison.Ordinal);
        Assert.DoesNotContain("Verification skipped", full, StringComparison.Ordinal);
    }
}
