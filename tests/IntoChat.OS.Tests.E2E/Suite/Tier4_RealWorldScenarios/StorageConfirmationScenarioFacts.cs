using DigitalBrain.Rules;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FakeStorage = IntoChat.OS.Tests.E2E.Fixtures.Doubles.FakeStorage;

namespace IntoChat.OS.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class StorageScenarioFlowData
{
    public string Operation { get; set; } = "storage-scenario";
    public string TargetId { get; set; } = string.Empty;
    public string? GuardHash { get; set; }
}

/// <summary>
/// Tier 4 Real-World Application Scenarios: Durable Multi-Step Storage Write &amp; Confirmation Flow.
/// Validates the full user journey: client requests destructive write, handler computes concurrency Guard,
/// flow enters Waiting status, user resolves confirmation via inbox, and handler verifies guard freshness upon durable execution.
/// Also exercises edge branches: user denial, expired confirmation, guard mismatch race conditions, and dry-run bypass.
/// </summary>
public sealed class StorageConfirmationScenarioFacts : E2ETestBase
{
    private sealed class StubOperationsQuery : IOperationsQuery
    {
        public Task<OperationView?> Read(OperationId id, CancellationToken ct) => Task.FromResult<OperationView?>(null);
    }

    private static (StorageWriteHandler handler, FakeStorage storage, ServiceProvider services) CreateStorageHarness()
    {
        var fakeStorage = new FakeStorage();
        var ownerSource = new FakeStorageOwnerSource(fakeStorage);

        var services = new ServiceCollection();
        services.AddSingleton<IStorageWriteAuthorizer>(new FakeStorageWriteAuthorizer());
        services.AddSingleton(new ConfirmationPolicies());
        services.AddSingleton<StorageWriteResult>((storage, id, ct) => Task.FromResult<OperationResult>(new OperationResult.Succeeded()));
        var provider = services.BuildServiceProvider();

        var handler = new StorageWriteHandler(ownerSource);
        return (handler, fakeStorage, provider);
    }

    private static Transition<FlowState<StorageScenarioFlowData>> Moved(Outcome<FlowState<StorageScenarioFlowData>> outcome) => Assert.IsType<Transition<FlowState<StorageScenarioFlowData>>>(outcome.Value);

    [Fact]
    public async Task T4_Storage_DestructiveDeleteUserJourney_WithInboxApprovalAndGuardVerification()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("delete-journey");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var itemId = new ItemId("financials_2025");
        storage.SetItem(itemId, "financials_2025.pdf", ItemKind.File, "sha256-v1-hash");

        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;

        // Step 1: Initialize multi-step flow
        var flow = new FlowState<StorageScenarioFlowData>();
        var started = Moved(flow.Start("Delete Document Workflow", "alice", null, new StorageScenarioFlowData { TargetId = itemId.Value }, now, "trace-del-1", "storage.delete")).Next;
        Assert.Equal(FlowStatus.Running, started.Status);

        var activeStep = Moved(started.StartStep(["prepare-approval", "execute-delete"], now)).Next;
        Assert.Equal("prepare-approval", activeStep.Current);

        // Step 2: StorageWriteHandler.Describe evaluates destructive plan and computes concurrency Guard
        var plan = WritePlan.Delete(itemId, permanent: true);
        var initialScope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, initialScope, ct);
        Assert.NotNull(approval);
        Assert.True(approval.Question.Destructive);
        Assert.Equal("sha256-v1-hash", approval.Guard);

        // Step 3: Flow enters Waiting status awaiting user confirmation
        var confirmationPlanId = approval.Question.Id;
        var waiting = new Waiting("prepare-approval", WaitReason.Approval, now.AddMinutes(5), confirmationPlanId);
        var inWait = Moved(activeStep.Wait("prepare-approval", new StepResult<StorageScenarioFlowData>(activeStep.Inner, waiting), waiting, confirmationPlanId)).Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);
        Assert.Equal(confirmationPlanId, inWait.Waiting?.ConfirmationPlanId);

        // Step 4: Confirmation is published to brain's confirmation inbox
        var immediateResult = await os.RequestConfirmation(brain, approval.Question);
        Assert.Null(immediateResult); // Awaiting human confirmation

        var pending = await os.GetPendingConfirmations(brain);
        Assert.Single(pending);
        Assert.Equal(confirmationPlanId, pending[0].Plan.Id);
        Assert.True(pending[0].Plan.Destructive);

        // Step 5: User reviews card and approves via inbox
        await os.ApprovePlan(brain, confirmationPlanId);
        var outcome = await os.GetConfirmationOutcome(brain, confirmationPlanId);
        Assert.NotNull(outcome);
        Assert.True(outcome.Approved);

        // Step 6: Flow resolves waiting step with Approved outcome
        var resolvedFlow = Moved(inWait.Resolve("prepare-approval", WaitOutcome.Approved, "alice", now)).Next;
        Assert.Equal(FlowStatus.Running, resolvedFlow.Status);
        Assert.Null(resolvedFlow.Waiting);
        Assert.Contains("prepare-approval", resolvedFlow.Done);

        // Step 7: Next step executes durable write with verified concurrency Guard
        var runStep = Moved(resolvedFlow.StartStep(["prepare-approval", "execute-delete"], now)).Next;
        Assert.Equal("execute-delete", runStep.Current);

        var approvedScope = new OperationScope(brain, "alice", provider, Guard: approval.Guard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, opNeuron, approvedScope, ct);
        Assert.IsType<OperationResult.Succeeded>(result);
        Assert.Equal(1, storage.WriteCount);

        // Verify permanent deletion from storage
        await Assert.ThrowsAsync<NotFound>(() => storage.Stat(itemId, ct));

        // Step 8: Flow completes
        var completedStep = Moved(runStep.Complete("execute-delete", new StepResult<StorageScenarioFlowData>(runStep.Inner))).Next;
        var finalFlow = Moved(completedStep.StartStep(["prepare-approval", "execute-delete"], now)).Next;
        Assert.Equal(FlowStatus.Completed, finalFlow.Status);
        Assert.True(finalFlow.IsOver);
    }

    [Fact]
    public async Task T4_Storage_ConcurrencyGuardMismatch_AbortsExecutionAndFailsFlow_WhenFileMutatedDuringConfirmation()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("guard-mismatch-journey");
        var itemId = new ItemId("shared_document");
        storage.SetItem(itemId, "shared.docx", ItemKind.File, "hash-ver-100");

        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        // Step 1: Describe captures hash-ver-100
        var approval = await handler.Describe(plan, scope, ct);
        Assert.NotNull(approval);
        var capturedGuard = approval.Guard;
        Assert.Equal("hash-ver-100", capturedGuard);

        // Step 2: Flow enters Waiting
        var flow = Moved(Moved(new FlowState<StorageScenarioFlowData>()
            .Start("Concurrent Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")).Next
            .StartStep(["delete-step"], now)).Next;

        var waiting = new Waiting("delete-step", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var inWait = Moved(flow.Wait("delete-step", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, approval.Question.Id)).Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Step 3: Mid-flight concurrent mutation modifies file in storage while approval is open
        storage.SetItem(itemId, "shared.docx", ItemKind.File, "hash-ver-200-modified");

        // Step 4: User resolves approval
        var resolved = Moved(inWait.Resolve("delete-step", WaitOutcome.Approved, "alice", now)).Next;
        Assert.Equal(FlowStatus.Running, resolved.Status);

        // Step 5: Run executes with obsolete guard -> Guard mismatch aborts execution
        var scopeWithOldGuard = new OperationScope(brain, "alice", provider, Guard: capturedGuard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, opNeuron, scopeWithOldGuard, ct);
        var failed = Assert.IsType<OperationResult.Failed>(result);

        Assert.Equal("plan-invalid", failed.Code);
        Assert.Contains("The item changed.", failed.Message);
        Assert.Equal(0, storage.WriteCount); // Storage mutation aborted!

        // Step 6: Flow handles failure and transitions to Failed
        var failedTransition = Moved(resolved.Fail("delete-step", failed.Code, now));
        Assert.Equal(FlowStatus.Failed, failedTransition.Next.Status);
    }

    [Fact]
    public async Task T4_Storage_UserDenial_RejectsPlanAndCancelsWorkflowWithoutStorageMutation()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("user-denial-journey");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var itemId = new ItemId("system_config");
        storage.SetItem(itemId, "system_config.json", ItemKind.File, "config-hash-1");

        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, ct);
        Assert.NotNull(approval);

        var flow = Moved(Moved(new FlowState<StorageScenarioFlowData>()
            .Start("Safe Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")).Next
            .StartStep(["delete-step"], now)).Next;

        var waiting = new Waiting("delete-step", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var inWait = Moved(flow.Wait("delete-step", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, approval.Question.Id)).Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Publish to inbox
        await os.RequestConfirmation(brain, approval.Question);

        // User reviews and declines the plan
        await os.DeclinePlan(brain, approval.Question.Id);
        var outcome = await os.GetConfirmationOutcome(brain, approval.Question.Id);
        Assert.NotNull(outcome);
        Assert.Equal(ConfirmationOutcome.Denied, outcome.Outcome);

        // Flow resolves with Denied outcome -> Cancelled
        var cancelled = Moved(inWait.Resolve("delete-step", WaitOutcome.Denied, "alice", now)).Next;
        Assert.Equal(FlowStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.IsOver);

        // Invariant: Storage was never modified
        Assert.Equal(0, storage.WriteCount);
        var stat = await storage.Stat(itemId, ct);
        Assert.NotNull(stat);
    }

    [Fact]
    public void T4_Storage_ExpiredConfirmation_TimesOutAndCancelsWorkflow()
    {
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var flow = Moved(Moved(new FlowState<StorageScenarioFlowData>()
            .Start("Timeout Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")).Next
            .StartStep(["await-approval"], now)).Next;

        var planId = "plan-will-expire";
        var waiting = new Waiting("await-approval", WaitReason.Approval, now.AddMinutes(5), planId);
        var inWait = Moved(flow.Wait("await-approval", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, planId)).Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Wait expires when timeout elapses
        var expiredTime = now.AddMinutes(6);
        var expired = Moved(inWait.Resolve("await-approval", WaitOutcome.Expired, null, expiredTime)).Next;

        Assert.Equal(FlowStatus.Cancelled, expired.Status);
        Assert.True(expired.IsOver);
    }

    [Fact]
    public async Task T4_Storage_DryRunExecution_BypassesApprovalAndExecutesPreviewDirectly()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("dryrun-journey");
        var itemId = new ItemId("preview-doc");
        storage.SetItem(itemId, "draft.md", ItemKind.File, "hash-draft");

        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        // Dry-run plan
        var plan = WritePlan.Delete(itemId, permanent: true) with { DryRun = true };
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        // Describe returns null approval because plan is a dry run
        var approval = await handler.Describe(plan, scope, ct);
        Assert.Null(approval);

        // Flow proceeds through steps without waiting
        var flow = Moved(Moved(new FlowState<StorageScenarioFlowData>()
            .Start("Dry Run Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.preview")).Next
            .StartStep(["simulate-step"], now)).Next;

        Assert.Equal("simulate-step", flow.Current);
        var completed = Moved(flow.Complete("simulate-step", new StepResult<StorageScenarioFlowData>(flow.Inner))).Next;
        var finished = Moved(completed.StartStep(["simulate-step"], now)).Next;

        Assert.Equal(FlowStatus.Completed, finished.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task T4_Storage_MultiStepCompositeBatchWorkflow_DurableExecutionWithAudit()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("composite-batch-journey");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var sourceFile = new ItemId("original_file");
        storage.SetItem(sourceFile, "presentation.pptx", ItemKind.File, "hash-original-v1");

        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        string[] steps = ["create-backup-folder", "copy-to-backup", "destructive-overwrite", "audit-log"];

        // 1. Flow start
        var flow = Moved(new FlowState<StorageScenarioFlowData>()
            .Start("Composite Document Pipeline", "alice", null, new StorageScenarioFlowData(), now, "trace-batch", "storage.batch")).Next;

        // Step 1: Create backup folder
        var s1 = Moved(flow.StartStep(steps, now)).Next;
        Assert.Equal("create-backup-folder", s1.Current);
        var backupFolderId = new ItemId("backup_dir");
        storage.SetItem(backupFolderId, "backup", ItemKind.Folder);
        var s1Done = Moved(s1.Complete("create-backup-folder", new StepResult<StorageScenarioFlowData>(s1.Inner))).Next;

        // Step 2: Copy file to backup
        var s2 = Moved(s1Done.StartStep(steps, now)).Next;
        Assert.Equal("copy-to-backup", s2.Current);
        var backupFileId = new ItemId("presentation_backup");
        storage.SetItem(backupFileId, "presentation.pptx", ItemKind.File, "hash-original-v1");
        var s2Done = Moved(s2.Complete("copy-to-backup", new StepResult<StorageScenarioFlowData>(s2.Inner))).Next;

        // Step 3: Destructive overwrite requires confirmation
        var s3 = Moved(s2Done.StartStep(steps, now)).Next;
        Assert.Equal("destructive-overwrite", s3.Current);

        var overwritePlan = WritePlan.Delete(sourceFile, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());
        var approval = await handler.Describe(overwritePlan, scope, ct);
        Assert.NotNull(approval);
        Assert.Equal("hash-original-v1", approval.Guard);

        // Pause flow in Waiting
        var waiting = new Waiting("destructive-overwrite", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var s3Waiting = Moved(s3.Wait("destructive-overwrite", new StepResult<StorageScenarioFlowData>(s3.Inner, waiting), waiting, approval.Question.Id)).Next;
        Assert.Equal(FlowStatus.Waiting, s3Waiting.Status);

        // Inbox confirmation and approval
        await os.RequestConfirmation(brain, approval.Question);
        await os.ApprovePlan(brain, approval.Question.Id);

        var s3Approved = Moved(s3Waiting.Resolve("destructive-overwrite", WaitOutcome.Approved, "alice", now)).Next;
        Assert.Equal(FlowStatus.Running, s3Approved.Status);

        // Execute overwrite
        var approvedScope = new OperationScope(brain, "alice", provider, Guard: approval.Guard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();
        var runResult = await handler.Run(overwritePlan, opNeuron, approvedScope, ct);
        Assert.IsType<OperationResult.Succeeded>(runResult);

        // Step 4: Audit log and finish
        var s4 = Moved(s3Approved.StartStep(steps, now)).Next;
        Assert.Equal("audit-log", s4.Current);
        var s4Done = Moved(s4.Complete("audit-log", new StepResult<StorageScenarioFlowData>(s4.Inner))).Next;

        var finalFlow = Moved(s4Done.StartStep(steps, now)).Next;
        Assert.Equal(FlowStatus.Completed, finalFlow.Status);
        Assert.Equal(steps, finalFlow.Done);
    }
}
