using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier3_CrossFeature;

public sealed class StorageFlowData
{
    public string Operation { get; set; } = "delete";
}

public sealed class StorageConfirmationFacts : E2ETestBase
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
    public async Task T3_Storage_Confirmation_EndToEndApprovalFlow_TransitionsThroughFlowStateAndExecutesWrite()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-confirm-e2e");
        var itemId = new ItemId("delete-file-doc");
        storage.SetItem(itemId, "contract.pdf", ItemKind.File, "initial-v1-hash");

        // 1. Initial FlowState
        var now = DateTimeOffset.UtcNow;
        var flow = new FlowState<StorageFlowData>();
        Assert.Equal(FlowStatus.Pending, flow.Status);

        var started = flow.Start("Storage Delete", "alice", null, new StorageFlowData(), now, "trace-1", "storage.delete");
        Assert.NotNull(started);
        var running = started.Value.Next;
        Assert.Equal(FlowStatus.Running, running.Status);

        var withStep = running.StartStep(["step-delete"], now);
        Assert.NotNull(withStep);
        var activeStep = withStep.Value.Next;
        Assert.Equal("step-delete", activeStep.Current);

        // 2. StorageWriteHandler.Describe produces approval with Guard
        var plan = WritePlan.Delete(itemId, permanent: true);
        var initialScope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, initialScope, TestContext.Current.CancellationToken);
        Assert.NotNull(approval);
        Assert.NotNull(approval.Guard);

        // 3. Flow enters Waiting status awaiting user confirmation
        const string planId = "plan-confirm-101";
        var waiting = new Waiting("step-delete", WaitReason.Approval, now.AddMinutes(10), planId);
        var inWait = activeStep.Wait("step-delete", new StepResult<StorageFlowData>(activeStep.Inner, waiting), waiting, planId);
        Assert.NotNull(inWait);
        var waitingFlow = inWait.Value.Next;
        Assert.Equal(FlowStatus.Waiting, waitingFlow.Status);
        Assert.Equal(planId, waitingFlow.Waiting?.ConfirmationPlanId);

        // 4. User approves confirmation plan
        var approved = waitingFlow.Resolve("step-delete", WaitOutcome.Approved, "alice", now);
        Assert.NotNull(approved);
        var resolvedFlow = approved.Value.Next;
        Assert.Equal(FlowStatus.Running, resolvedFlow.Status);
        Assert.Null(resolvedFlow.Waiting);
        Assert.Contains("step-delete", resolvedFlow.Done);

        // 5. StorageWriteHandler.Run executes with approved Guard
        var approvedScope = new OperationScope(brain, "alice", provider, Guard: approval.Guard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, opNeuron, approvedScope, TestContext.Current.CancellationToken);
        Assert.IsType<OperationResult.Succeeded>(result);
        Assert.Equal(1, storage.WriteCount);

        // 6. Flow completes all steps
        var completed = resolvedFlow.StartStep(["step-delete"], now);
        Assert.NotNull(completed);
        var finalFlow = completed.Value.Next;
        Assert.Equal(FlowStatus.Completed, finalFlow.Status);
    }

    [Fact]
    public async Task T3_Storage_Confirmation_ConcurrencyGuardMismatch_FailsWithPlanInvalid_WhenFileMutatesMidFlight()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-guard-mismatch");
        var itemId = new ItemId("mutating-file");
        storage.SetItem(itemId, "shared.docx", ItemKind.File, "stamp-v1");

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        // Approval captures stamp-v1
        var approval = await handler.Describe(plan, scope, TestContext.Current.CancellationToken);
        Assert.NotNull(approval);
        var capturedGuard = approval.Guard;
        Assert.NotNull(capturedGuard);

        // Flow enters Waiting
        var now = DateTimeOffset.UtcNow;
        var flow = new FlowState<StorageFlowData>()
            .Start("Storage Delete", "alice", null, new StorageFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["delete-step"], now)!.Value.Next;

        var waiting = new Waiting("delete-step", WaitReason.Approval, now.AddMinutes(5), "p-mutate");
        var inWait = flow.Wait("delete-step", new StepResult<StorageFlowData>(flow.Inner, waiting), waiting, "p-mutate")!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Mid-flight concurrent mutation modifies file before write execution
        storage.SetItem(itemId, "shared.docx", ItemKind.File, "stamp-v2-mutated");

        // Approval is granted for old guard
        var resolved = inWait.Resolve("delete-step", WaitOutcome.Approved, "alice", now)!.Value.Next;
        Assert.Equal(FlowStatus.Running, resolved.Status);

        // Run executes with obsolete guard -> Guard mismatch detection
        var scopeWithOldGuard = new OperationScope(brain, "alice", provider, Guard: capturedGuard, new StubOperationsQuery());
        var opNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, opNeuron, scopeWithOldGuard, TestContext.Current.CancellationToken);
        var failed = Assert.IsType<OperationResult.Failed>(result);

        Assert.Equal("plan-invalid", failed.Code);
        Assert.Contains("The item changed.", failed.Message);
        Assert.Equal(0, storage.WriteCount);

        // Flow transitions to Failed
        var failedTransition = resolved.Fail("delete-step", failed.Code, now);
        Assert.NotNull(failedTransition);
        Assert.Equal(FlowStatus.Failed, failedTransition.Value.Next.Status);
    }

    [Fact]
    public async Task T3_Storage_Confirmation_UserDenial_AbortsWriteAndTransitionsFlowToFailed()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-denial");
        var itemId = new ItemId("important-folder");
        storage.SetItem(itemId, "projects", ItemKind.Folder, "hash-folder");

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "bob", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, TestContext.Current.CancellationToken);
        Assert.NotNull(approval);

        var now = DateTimeOffset.UtcNow;
        var flow = new FlowState<StorageFlowData>()
            .Start("Storage Delete", "bob", null, new StorageFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["step-del"], now)!.Value.Next;

        var waiting = new Waiting("step-del", WaitReason.Approval, now.AddMinutes(5), "p-deny");
        var inWait = flow.Wait("step-del", new StepResult<StorageFlowData>(flow.Inner, waiting), waiting, "p-deny")!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // User denies approval -> Resolve with WaitOutcome.Denied cancels flow
        var denied = inWait.Resolve("step-del", WaitOutcome.Denied, "bob", now)!.Value.Next;

        Assert.Equal(FlowStatus.Cancelled, denied.Status);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public void T3_Storage_Confirmation_ApprovalExpiration_TransitionsFlowToTimedOutAndBlocksExecution()
    {
        var now = DateTimeOffset.UtcNow;
        var flow = new FlowState<StorageFlowData>()
            .Start("Storage Delete", "bob", null, new StorageFlowData(), now, null, "storage.delete")!.Value.Next
            .StartStep(["step-timeout"], now)!.Value.Next;

        var waiting = new Waiting("step-timeout", WaitReason.Approval, now.AddMinutes(5), "p-expire");
        var inWait = flow.Wait("step-timeout", new StepResult<StorageFlowData>(flow.Inner, waiting), waiting, "p-expire")!.Value.Next;
        Assert.Equal(FlowStatus.Waiting, inWait.Status);

        // Expired transition cancels flow
        var expired = inWait.Resolve("step-timeout", WaitOutcome.Expired, null, now)!.Value.Next;

        Assert.Equal(FlowStatus.Cancelled, expired.Status);
    }

    [Fact]
    public async Task T3_Storage_Confirmation_DryRunPlan_BypassesConfirmationAndApprovalGeneration()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-dryrun-flow");
        var itemId = new ItemId("preview-file");
        storage.SetItem(itemId, "draft.txt", ItemKind.File, "hash-draft");

        // Dry run plan bypasses approval
        var plan = WritePlan.Delete(itemId, permanent: true) with { DryRun = true };
        var scope = new OperationScope(brain, "alice", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, TestContext.Current.CancellationToken);
        Assert.Null(approval);

        // Flow proceeds without waiting
        var now = DateTimeOffset.UtcNow;
        var flow = new FlowState<StorageFlowData>()
            .Start("Storage Preview", "alice", null, new StorageFlowData(), now, null, "storage.preview")!.Value.Next
            .StartStep(["preview-step"], now)!.Value.Next;

        var completedStep = flow.Complete("preview-step", new StepResult<StorageFlowData>(flow.Inner))!.Value.Next;
        var completedFlow = completedStep.StartStep(["preview-step"], now)!.Value.Next;

        Assert.Equal(FlowStatus.Completed, completedFlow.Status);
    }
}
