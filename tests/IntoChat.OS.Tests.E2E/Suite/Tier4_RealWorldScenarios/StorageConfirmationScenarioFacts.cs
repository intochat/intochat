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

    [Fact]
    public async Task T4_Storage_DestructiveDeleteUserJourney_WithInboxApprovalAndGuardVerification()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("delete-journey");
        var itemId = new ItemId("financials_2025");
        storage.SetItem(itemId, "financials_2025.pdf", ItemKind.File, "sha256-v1-hash");

        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;

        // Step 1: Initialize multi-step flow
        var flow = new FlowState<StorageScenarioFlowData>();
        var started = flow.Start("Delete Document Workflow", "alice", null, new StorageScenarioFlowData { TargetId = itemId.Value }, now, "trace-del-1", "storage.delete")!.Value.Next;
        Assert.Equal(FlowStatus.Running, started.Status);

        var activeStep = started.StartStep(["prepare-approval", "execute-delete"], now)!.Value.Next;
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
        var inWait = activeStep.Wait("prepare-approval", new StepResult<StorageScenarioFlowData>(activeStep.Inner, waiting), waiting, confirmationPlanId)!.Value.Next;
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
        var resolvedFlow = inWait.Resolve("prepare-approval", WaitOutcome.Approved, "alice", now)!.Value.Next;
        Assert.Equal(FlowStatus.Running, resolvedFlow.Status);
        Assert.Null(resolvedFlow.Waiting);
        Assert.Contains("prepare-approval", resolvedFlow.Done);

        // Step 7: Next step executes durable write with verified concurrency Guard
        var runStep = resolvedFlow.StartStep(["prepare-approval", "execute-delete"], now)!.Value.Next;
        Assert.Equal("execute-delete", runStep.Current);

        var approvedScope = new OperationScope(brain, "alice", provider, Guard: approval.Guard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, opNeuron, approvedScope, ct);
        Assert.IsType<OperationResult.Succeeded>(result);
        Assert.Equal(1, storage.WriteCount);

        // Verify permanent deletion from storage
        await Assert.ThrowsAsync<NotFound>(() => storage.Stat(itemId, ct));

        // Step 8: Flow completes
        var completedStep = runStep.Complete("execute-delete", new StepResult<StorageScenarioFlowData>(runStep.Inner))!.Value.Next;
        var finalFlow = completedStep.StartStep(["prepare-approval", "execute-delete"], now)!.Value.Next;
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
        var flow = new FlowState<StorageScenarioFlowData>()
            .Start("Concurrent Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["delete-step"], now)!.Value.Next;

        var waiting = new Waiting("delete-step", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var inWait = flow.Wait("delete-step", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, approval.Question.Id)!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Step 3: Mid-flight concurrent mutation modifies file in storage while approval is open
        storage.SetItem(itemId, "shared.docx", ItemKind.File, "hash-ver-200-modified");

        // Step 4: User resolves approval
        var resolved = inWait.Resolve("delete-step", WaitOutcome.Approved, "alice", now)!.Value.Next;
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
        var failedTransition = resolved.Fail("delete-step", failed.Code, now);
        Assert.NotNull(failedTransition);
        Assert.Equal(FlowStatus.Failed, failedTransition.Value.Next.Status);
    }

    [Fact]
    public async Task T4_Storage_UserDenial_RejectsPlanAndCancelsWorkflowWithoutStorageMutation()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("user-denial-journey");
        var itemId = new ItemId("system_config");
        storage.SetItem(itemId, "system_config.json", ItemKind.File, "config-hash-1");

        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        var ct = TestContext.Current.CancellationToken;

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, ct);
        Assert.NotNull(approval);

        var flow = new FlowState<StorageScenarioFlowData>()
            .Start("Safe Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["delete-step"], now)!.Value.Next;

        var waiting = new Waiting("delete-step", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var inWait = flow.Wait("delete-step", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, approval.Question.Id)!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Publish to inbox
        await os.RequestConfirmation(brain, approval.Question);

        // User reviews and declines the plan
        await os.DeclinePlan(brain, approval.Question.Id);
        var outcome = await os.GetConfirmationOutcome(brain, approval.Question.Id);
        Assert.NotNull(outcome);
        Assert.Equal(ConfirmationOutcome.Denied, outcome.Outcome);

        // Flow resolves with Denied outcome -> Cancelled
        var cancelled = inWait.Resolve("delete-step", WaitOutcome.Denied, "alice", now)!.Value.Next;
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
        var flow = new FlowState<StorageScenarioFlowData>()
            .Start("Timeout Delete Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["await-approval"], now)!.Value.Next;

        var planId = "plan-will-expire";
        var waiting = new Waiting("await-approval", WaitReason.Approval, now.AddMinutes(5), planId);
        var inWait = flow.Wait("await-approval", new StepResult<StorageScenarioFlowData>(flow.Inner, waiting), waiting, planId)!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Wait expires when timeout elapses
        var expiredTime = now.AddMinutes(6);
        var expired = inWait.Resolve("await-approval", WaitOutcome.Expired, null, expiredTime)!.Value.Next;

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
        var flow = new FlowState<StorageScenarioFlowData>()
            .Start("Dry Run Flow", "alice", null, new StorageScenarioFlowData(), now, null, "storage.preview")!.Value.Next
            .StartStep(["simulate-step"], now)!.Value.Next;

        Assert.Equal("simulate-step", flow.Current);
        var completed = flow.Complete("simulate-step", new StepResult<StorageScenarioFlowData>(flow.Inner))!.Value.Next;
        var finished = completed.StartStep(["simulate-step"], now)!.Value.Next;

        Assert.Equal(FlowStatus.Completed, finished.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public async Task T4_Storage_MultiStepCompositeBatchWorkflow_DurableExecutionWithAudit()
    {
        await using var os = TestOS.Local();
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("composite-batch-journey");
        var sourceFile = new ItemId("original_file");
        storage.SetItem(sourceFile, "presentation.pptx", ItemKind.File, "hash-original-v1");

        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        string[] steps = ["create-backup-folder", "copy-to-backup", "destructive-overwrite", "audit-log"];

        // 1. Flow start
        var flow = new FlowState<StorageScenarioFlowData>()
            .Start("Composite Document Pipeline", "alice", null, new StorageScenarioFlowData(), now, "trace-batch", "storage.batch")!.Value.Next;

        // Step 1: Create backup folder
        var s1 = flow.StartStep(steps, now)!.Value.Next;
        Assert.Equal("create-backup-folder", s1.Current);
        var backupFolderId = new ItemId("backup_dir");
        storage.SetItem(backupFolderId, "backup", ItemKind.Folder);
        var s1Done = s1.Complete("create-backup-folder", new StepResult<StorageScenarioFlowData>(s1.Inner))!.Value.Next;

        // Step 2: Copy file to backup
        var s2 = s1Done.StartStep(steps, now)!.Value.Next;
        Assert.Equal("copy-to-backup", s2.Current);
        var backupFileId = new ItemId("presentation_backup");
        storage.SetItem(backupFileId, "presentation.pptx", ItemKind.File, "hash-original-v1");
        var s2Done = s2.Complete("copy-to-backup", new StepResult<StorageScenarioFlowData>(s2.Inner))!.Value.Next;

        // Step 3: Destructive overwrite requires confirmation
        var s3 = s2Done.StartStep(steps, now)!.Value.Next;
        Assert.Equal("destructive-overwrite", s3.Current);

        var overwritePlan = WritePlan.Delete(sourceFile, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());
        var approval = await handler.Describe(overwritePlan, scope, ct);
        Assert.NotNull(approval);
        Assert.Equal("hash-original-v1", approval.Guard);

        // Pause flow in Waiting
        var waiting = new Waiting("destructive-overwrite", WaitReason.Approval, now.AddMinutes(5), approval.Question.Id);
        var s3Waiting = s3.Wait("destructive-overwrite", new StepResult<StorageScenarioFlowData>(s3.Inner, waiting), waiting, approval.Question.Id)!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, s3Waiting.Status);

        // Inbox confirmation and approval
        await os.RequestConfirmation(brain, approval.Question);
        await os.ApprovePlan(brain, approval.Question.Id);

        var s3Approved = s3Waiting.Resolve("destructive-overwrite", WaitOutcome.Approved, "alice", now)!.Value.Next;
        Assert.Equal(FlowStatus.Running, s3Approved.Status);

        // Execute overwrite
        var approvedScope = new OperationScope(brain, "alice", provider, Guard: approval.Guard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();
        var runResult = await handler.Run(overwritePlan, opNeuron, approvedScope, ct);
        Assert.IsType<OperationResult.Succeeded>(runResult);

        // Step 4: Audit log and finish
        var s4 = s3Approved.StartStep(steps, now)!.Value.Next;
        Assert.Equal("audit-log", s4.Current);
        var s4Done = s4.Complete("audit-log", new StepResult<StorageScenarioFlowData>(s4.Inner))!.Value.Next;

        var finalFlow = s4Done.StartStep(steps, now)!.Value.Next;
        Assert.Equal(FlowStatus.Completed, finalFlow.Status);
        Assert.Equal(steps, finalFlow.Done);
    }
}
