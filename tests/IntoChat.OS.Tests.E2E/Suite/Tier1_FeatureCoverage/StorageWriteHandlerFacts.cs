using DigitalBrain.Rules;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Operations;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

public sealed class SampleCustomFlowData
{
    public string StatusMessage { get; set; } = "init";

    public SampleCustomFlowData() { }
    public SampleCustomFlowData(string statusMessage)
    {
        StatusMessage = statusMessage;
    }
}

public sealed class StorageWriteHandlerFacts : E2ETestBase
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

    private static Transition<FlowState<SampleCustomFlowData>> Moved(Outcome<FlowState<SampleCustomFlowData>> outcome) => Assert.IsType<Transition<FlowState<SampleCustomFlowData>>>(outcome.Value);

    [Fact]
    public async Task T1_Storage_WriteHandler_Describe_DryRunReturnsNullApproval()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-dry");
        var itemId = new ItemId("file-delete-1");
        storage.SetItem(itemId, "delete-me.txt", ItemKind.File, "hash-1");

        var plan = WritePlan.Delete(itemId, permanent: true) with { DryRun = true };
        var scope = new OperationScope(brain, "test-user", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, CancellationToken.None);
        Assert.Null(approval);
    }

    [Fact]
    public async Task T1_Storage_WriteHandler_Describe_DestructivePlanReturnsApprovalWithConcurrencyGuard()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-guard");
        var itemId = new ItemId("file-perm-delete");
        storage.SetItem(itemId, "important.doc", ItemKind.File, "hash-v100");

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "test-user", provider, Guard: null, new StubOperationsQuery());

        var approval = await handler.Describe(plan, scope, CancellationToken.None);
        Assert.NotNull(approval);
        Assert.True(approval.Question.Destructive);
        Assert.Equal("hash-v100", approval.Guard);
    }

    [Fact]
    public async Task T1_Storage_WriteHandler_Run_MismatchedGuard_FailsWithPlanInvalid()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-mismatch");
        var itemId = new ItemId("file-change");
        // Storage has current hash "hash-v200"
        storage.SetItem(itemId, "file.txt", ItemKind.File, "hash-v200");

        var plan = WritePlan.Delete(itemId, permanent: true);
        // Scope carries stale approved guard "hash-v100"
        var scope = new OperationScope(brain, "test-user", provider, Guard: "hash-v100", new StubOperationsQuery());
        var fakeNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, fakeNeuron, scope, CancellationToken.None);

        var failed = Assert.IsType<OperationResult.Failed>(result);
        Assert.Equal("plan-invalid", failed.Code);
        Assert.Equal("The item changed.", failed.Message);
        Assert.Equal(0, storage.WriteCount); // Storage write was never called
    }

    [Fact]
    public async Task T1_Storage_WriteHandler_Run_MatchingGuard_ExecutesWriteAndReportsProgress()
    {
        var (handler, storage, provider) = CreateStorageHarness();
        var brain = NewBrain("storage-match");
        var itemId = new ItemId("file-exec");
        storage.SetItem(itemId, "file.txt", ItemKind.File, "hash-v100");

        var plan = WritePlan.Delete(itemId, permanent: true);
        var scope = new OperationScope(brain, "test-user", provider, Guard: "hash-v100", new StubOperationsQuery());
        var fakeNeuron = new FakeOperationNeuron();

        var result = await handler.Run(plan, fakeNeuron, scope, CancellationToken.None);

        Assert.IsType<OperationResult.Succeeded>(result);
        Assert.Equal(1, storage.WriteCount);
    }

    [Fact]
    public void T1_Storage_FlowState_LifecycleTransitions_AdvanceFromPendingToRunningToCompleted()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new FlowState<SampleCustomFlowData>();
        Assert.Equal(FlowStatus.Pending, state.Status);

        // Transition 1: Start
        var started = Moved(state.Start("Sample Flow", "alice", null, new SampleCustomFlowData("started"), now, "trace-1", "sample.flow"));
        Assert.Equal(FlowStatus.Running, started.Next.Status);
        Assert.Single(started.Signals);

        // Transition 2: StartStep 1
        var step1 = Moved(started.Next.StartStep(["step1", "step2"], now));
        Assert.Equal("step1", step1.Next.Current);

        // Transition 3: Complete step 1
        var comp1 = Moved(step1.Next.Complete("step1", new StepResult<SampleCustomFlowData>(new SampleCustomFlowData("step1 done"))));
        Assert.Null(comp1.Next.Current);
        Assert.Contains("step1", comp1.Next.Done);

        // Transition 4: StartStep 2
        var step2 = Moved(comp1.Next.StartStep(["step1", "step2"], now));
        Assert.Equal("step2", step2.Next.Current);

        // Transition 5: Complete step 2
        var comp2 = Moved(step2.Next.Complete("step2", new StepResult<SampleCustomFlowData>(new SampleCustomFlowData("step2 done"))));
        Assert.Contains("step2", comp2.Next.Done);

        // Transition 6: Final check - all steps done completes flow
        var finished = Moved(comp2.Next.StartStep(["step1", "step2"], now));
        Assert.Equal(FlowStatus.Completed, finished.Next.Status);
        Assert.True(finished.Next.IsOver);
    }

    [Fact]
    public void T1_Storage_FlowState_WaitAndResolve_TransitionsThroughAwaitingApprovalAndArrived()
    {
        var now = DateTimeOffset.UtcNow;
        var state = Moved(Moved(new FlowState<SampleCustomFlowData>()
            .Start("Approval Flow", "alice", null, new SampleCustomFlowData(), now, "trace-1", "sample.flow")).Next
            .StartStep(["confirm-step"], now)).Next;

        // Step waits for approval
        var waiting = Moved(state.Wait(
            "confirm-step",
            new StepResult<SampleCustomFlowData>(new SampleCustomFlowData("waiting")),
            new Waiting("confirm-step", WaitReason.Approval, null, "plan_999"),
            "plan_999"));

        Assert.Equal(FlowStatus.Waiting, waiting.Next.Status);
        Assert.NotNull(waiting.Next.Waiting);
        Assert.Equal("plan_999", waiting.Next.Waiting.ConfirmationPlanId);

        // Resolve approval as Approved
        var resolved = Moved(waiting.Next.Resolve(
            "confirm-step",
            WaitOutcome.Approved,
            "alice@intochat.io",
            now));

        Assert.Equal(FlowStatus.Running, resolved.Next.Status);
        Assert.Null(resolved.Next.Waiting);
        Assert.Contains("confirm-step", resolved.Next.Done);
    }
}
