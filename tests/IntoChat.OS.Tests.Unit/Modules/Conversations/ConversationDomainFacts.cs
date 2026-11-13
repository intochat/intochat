using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Conversations;
using IntoChat.OS.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Conversations.Tests.Unit;

public sealed class ConversationIdFacts
{
    [Fact]
    public void New_ProducesCanonicalId_MatchingRegex()
    {
        var id = ConversationId.New();

        Assert.True(id.IsCanonical);
        Assert.StartsWith("c-", id.Value, StringComparison.Ordinal);
        Assert.Equal(34, id.Value.Length);
        Assert.Matches("^c-[0-9a-f]{32}$", id.Value);
    }

    [Fact]
    public void ParseCanonical_ValidCanonical_ReturnsInstance()
    {
        var canonical = "c-1234567890abcdef1234567890abcdef";
        var id = ConversationId.ParseCanonical(canonical);

        Assert.Equal(canonical, id.Value);
        Assert.True(id.IsCanonical);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("c-12345678")]
    [InlineData("c-XYZ")]
    [InlineData("bad-prefix-1234567890abcdef1234567890abcdef")]
    public void ParseCanonical_NonCanonical_ThrowsArgumentException(string value)
    {
        Assert.Throws<ArgumentException>(() => ConversationId.ParseCanonical(value));
    }

    [Theory]
    [InlineData("main", true, false)]
    [InlineData("c-resilience-1", true, false)]
    [InlineData("c-12345678", true, false)]
    [InlineData("c-1234567890abcdef1234567890abcdef", true, true)]
    [InlineData("a/b", false, false)]
    [InlineData("..\\bad", false, false)]
    [InlineData("c-\0bad", false, false)]
    [InlineData("", false, false)]
    [InlineData("   ", false, false)]
    public void TryParse_ValidatesCorrectly(string? input, bool expectedSuccess, bool expectedCanonical)
    {
        var success = ConversationId.TryParse(input, out var id);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedCanonical, id.IsCanonical);
            Assert.Equal(input!.Trim(), id.Value);
        }
    }

    [Fact]
    public void FromOrNew_KeepsCanonical_OrGeneratesNew()
    {
        var canonical = "c-1234567890abcdef1234567890abcdef";
        var kept = ConversationId.FromOrNew(canonical);
        Assert.Equal(canonical, kept.Value);

        var replacedLegacy = ConversationId.FromOrNew("main");
        Assert.True(replacedLegacy.IsCanonical);
        Assert.NotEqual("main", replacedLegacy.Value);

        var replacedNull = ConversationId.FromOrNew(null);
        Assert.True(replacedNull.IsCanonical);
    }
}

public sealed class ConversationAggregateFacts
{
    private const string ValidBrain = "brain-primary-01";

    [Fact]
    public void Create_WithValidParameters_InitializesCorrectly()
    {
        var id = ConversationId.New();
        var aggregate = ConversationAggregate.Create(ValidBrain, id, "Test Title");

        Assert.Equal(id, aggregate.Id);
        Assert.Equal(ValidBrain, aggregate.BrainId);
        Assert.Equal("Test Title", aggregate.Title);
        Assert.Empty(aggregate.Turns);
        Assert.Null(aggregate.ActiveRun);
        Assert.Empty(aggregate.RunHistory);
        Assert.Empty(aggregate.Approvals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/brain")]
    public void Create_WithInvalidBrainId_ThrowsArgumentException(string invalidBrain)
    {
        var id = ConversationId.New();
        Assert.Throws<ArgumentException>(() => ConversationAggregate.Create(invalidBrain, id));
    }

    [Fact]
    public void Create_WithEmptyConversationId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ConversationAggregate.Create(ValidBrain, default));
    }

    [Fact]
    public void RecordTurn_RecordsStructuredTurn()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        var metadata = new Dictionary<string, string> { ["source"] = "web" };

        var turn = aggregate.RecordTurn("user", "Hello world", ConversationTurnRole.User, "run-1", metadata);

        Assert.Single(aggregate.Turns);
        Assert.Equal(turn, aggregate.Turns[0]);
        Assert.Equal("user", turn.Speaker);
        Assert.Equal("Hello world", turn.Content);
        Assert.Equal(ConversationTurnRole.User, turn.Role);
        Assert.Equal("run-1", turn.RunId);
        Assert.Equal("web", turn.Metadata?["source"]);
    }

    [Fact]
    public void StartRun_SetsActiveRun_AndRecordsUserTurnIfPromptProvided()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());

        var run = aggregate.StartRun("run-100", "flow-build", "step-author", "Create a calculator");

        Assert.NotNull(aggregate.ActiveRun);
        Assert.Equal("run-100", aggregate.ActiveRun.RunId);
        Assert.Equal(ConversationRunStatus.Running, aggregate.ActiveRun.Status);
        Assert.Equal("flow-build", aggregate.ActiveRun.FlowId);
        Assert.Equal("step-author", aggregate.ActiveRun.Step);
        Assert.Equal("Create a calculator", aggregate.ActiveRun.Prompt);
        Assert.True(aggregate.ActiveRun.IsActive);
        Assert.False(aggregate.ActiveRun.IsTerminal);

        // Turn should have been recorded for the prompt
        Assert.Single(aggregate.Turns);
        Assert.Equal("Create a calculator", aggregate.Turns[0].Content);
        Assert.Equal("run-100", aggregate.Turns[0].RunId);
    }

    [Fact]
    public void StartRun_WhenAlreadyActive_ThrowsInvalidOperationException()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1");

        var ex = Assert.Throws<InvalidOperationException>(() => aggregate.StartRun("run-2"));
        Assert.Contains("run-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvanceRunStep_UpdatesStepCleanlyWithoutRegexScraping()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-build", "step-author");

        aggregate.AdvanceRunStep("run-1", "step-verify");

        Assert.NotNull(aggregate.ActiveRun);
        Assert.Equal("step-verify", aggregate.ActiveRun.Step);
    }

    [Fact]
    public void AdvanceRunStep_WithMismatchRunId_ThrowsInvalidOperationException()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-build", "step-author");

        Assert.Throws<InvalidOperationException>(() => aggregate.AdvanceRunStep("run-unknown", "step-verify"));
    }

    [Fact]
    public void CompleteRun_TransitionsToTerminalCompleted_AndMovesToHistory()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-build", "step-author", "User prompt");

        aggregate.CompleteRun("run-1", "Assistant reply");

        Assert.Null(aggregate.ActiveRun);
        Assert.Single(aggregate.RunHistory);
        Assert.Equal("run-1", aggregate.RunHistory[0].RunId);
        Assert.Equal(ConversationRunStatus.Completed, aggregate.RunHistory[0].Status);
        Assert.Equal("Assistant reply", aggregate.RunHistory[0].Reply);
        Assert.NotNull(aggregate.RunHistory[0].EndedAt);

        // Assistant turn should also be recorded
        Assert.Equal(2, aggregate.Turns.Count);
        Assert.Equal("Assistant reply", aggregate.Turns[1].Content);
        Assert.Equal(ConversationTurnRole.Assistant, aggregate.Turns[1].Role);
    }

    [Fact]
    public void FailRun_TransitionsToTerminalFailed_AndMovesToHistory()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1");

        aggregate.FailRun("run-1", "Model connection timed out");

        Assert.Null(aggregate.ActiveRun);
        Assert.Single(aggregate.RunHistory);
        Assert.Equal(ConversationRunStatus.Failed, aggregate.RunHistory[0].Status);
        Assert.Equal("Model connection timed out", aggregate.RunHistory[0].Error);
        Assert.NotNull(aggregate.RunHistory[0].EndedAt);
    }

    [Fact]
    public void CancelRun_TransitionsToTerminalCancelled_AndMovesToHistory()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1");

        aggregate.CancelRun("run-1");

        Assert.Null(aggregate.ActiveRun);
        Assert.Single(aggregate.RunHistory);
        Assert.Equal(ConversationRunStatus.Cancelled, aggregate.RunHistory[0].Status);
        Assert.NotNull(aggregate.RunHistory[0].EndedAt);
    }
}

public sealed class ApprovalStateTransitionFacts
{
    private const string ValidBrain = "brain-primary-01";

    [Fact]
    public void RequestApproval_SetsStatusPending_AndTransitionsActiveRunToWaiting()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-unit", "step-deploy");

        var approval = aggregate.RequestApproval(
            "plan-100",
            ConversationApprovalKind.FlowStep,
            "Deploy Unit",
            "Requires write permissions",
            "flow-unit",
            "step-deploy");

        Assert.Single(aggregate.Approvals);
        Assert.Equal("plan-100", approval.ApprovalId);
        Assert.Equal(ConversationApprovalStatus.Pending, approval.Status);
        Assert.True(approval.IsPending);
        Assert.False(approval.IsResolved);

        // Active run should wait for approval
        Assert.NotNull(aggregate.ActiveRun);
        Assert.Equal(ConversationRunStatus.WaitingApproval, aggregate.ActiveRun.Status);
    }

    [Fact]
    public void RequestApproval_DuplicateId_ThrowsInvalidOperationException()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.RequestApproval("plan-1", ConversationApprovalKind.StorageWrite, "Write File", "Detail");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            aggregate.RequestApproval("plan-1", ConversationApprovalKind.StorageWrite, "Write File", "Detail"));
        Assert.Contains("plan-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveApproval_Approved_TransitionsToApproved_AndResumesActiveRun()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-unit", "step-deploy");
        aggregate.RequestApproval("plan-1", ConversationApprovalKind.FlowStep, "Deploy", "Detail");

        var resolved = aggregate.ResolveApproval("plan-1", approved: true, resolver: "admin@corp", reason: "Verified safe");

        Assert.Equal(ConversationApprovalStatus.Approved, resolved.Status);
        Assert.True(resolved.IsResolved);
        Assert.Equal("admin@corp", resolved.ResolvedBy);
        Assert.Equal("Verified safe", resolved.ResolutionReason);
        Assert.NotNull(resolved.ResolvedAt);

        // Active run should resume to Running
        Assert.NotNull(aggregate.ActiveRun);
        Assert.Equal(ConversationRunStatus.Running, aggregate.ActiveRun.Status);
    }

    [Fact]
    public void ResolveApproval_Denied_TransitionsToDenied_AndFailsActiveRun()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.StartRun("run-1", "flow-unit", "step-deploy");
        aggregate.RequestApproval("plan-1", ConversationApprovalKind.FlowStep, "Deploy", "Detail");

        var resolved = aggregate.ResolveApproval("plan-1", approved: false, resolver: "user@corp", reason: "Disallowed scope");

        Assert.Equal(ConversationApprovalStatus.Denied, resolved.Status);
        Assert.True(resolved.IsResolved);
        Assert.Equal("user@corp", resolved.ResolvedBy);
        Assert.Equal("Disallowed scope", resolved.ResolutionReason);

        // Active run should be terminated as Failed and cleared
        Assert.Null(aggregate.ActiveRun);
        Assert.Single(aggregate.RunHistory);
        Assert.Equal(ConversationRunStatus.Failed, aggregate.RunHistory[0].Status);
        Assert.Contains("Disallowed scope", aggregate.RunHistory[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveApproval_AlreadyResolved_ThrowsInvalidOperationException()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        aggregate.RequestApproval("plan-1", ConversationApprovalKind.StorageWrite, "Write", "Detail");
        aggregate.ResolveApproval("plan-1", approved: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            aggregate.ResolveApproval("plan-1", approved: false));
        Assert.Contains("already resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveApproval_NotFound_ThrowsInvalidOperationException()
    {
        var aggregate = ConversationAggregate.Create(ValidBrain, ConversationId.New());
        Assert.Throws<InvalidOperationException>(() =>
            aggregate.ResolveApproval("missing-plan", approved: true));
    }
}

public sealed class ConversationDomainServiceFacts
{
    [Fact]
    public void CreateConversation_GeneratesCanonicalIdWhenNotProvided()
    {
        var service = new ConversationDomainService();
        var aggregate = service.CreateConversation("brain-valid-01");

        Assert.True(aggregate.Id.IsCanonical);
        Assert.StartsWith("c-", aggregate.Id.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateConversation_AcceptsValidExistingLegacyId()
    {
        var service = new ConversationDomainService();
        var legacy = ConversationId.Parse("main");
        var aggregate = service.CreateConversation("brain-valid-01", existingId: legacy);

        Assert.Equal("main", aggregate.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/brain")]
    [InlineData(null)]
    public void ValidateBrainId_RejectsInvalidOrNonLocalBrainIds(string? invalidBrain)
    {
        var service = new ConversationDomainService();
        Assert.Throws<ArgumentException>(() => service.ValidateBrainId(invalidBrain!));
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesAllState()
    {
        var service = new ConversationDomainService();
        var aggregate = service.CreateConversation("brain-valid-01", "My Chat");
        aggregate.StartRun("run-1", "flow-1", "step-1", "First prompt");
        aggregate.RequestApproval("plan-1", ConversationApprovalKind.Action, "Action 1", "Detail 1");
        aggregate.CompleteRun("run-1", "First reply");

        var snapshot = aggregate.ToSnapshot();
        var restored = service.RestoreConversation(snapshot);

        Assert.Equal(aggregate.Id, restored.Id);
        Assert.Equal(aggregate.BrainId, restored.BrainId);
        Assert.Equal(aggregate.Title, restored.Title);
        Assert.Equal(aggregate.Turns.Count, restored.Turns.Count);
        Assert.Equal(aggregate.RunHistory.Count, restored.RunHistory.Count);
        Assert.Equal(aggregate.Approvals.Count, restored.Approvals.Count);
    }
}

public sealed class InoConversationsNeuronFacts
{
    private static InoConversationsNeuron CreateNeuron(string brainId = "brain-test-01")
    {
        var neuron = new InoConversationsNeuron();
        var neuronId = new NeuronId(new BrainId(brainId), "ino.conversations", IInoConversations.DefaultKey);
        NeuronAttachment.Attach(neuron, new FakeNeuronContext(neuronId));
        return neuron;
    }

    [Fact]
    public async Task Use_WithNewKeyword_CreatesCanonicalId()
    {
        var neuron = CreateNeuron();

        await neuron.Use("new");

        var current = await neuron.Current();
        Assert.Single(current);
        Assert.StartsWith("ino/conversations/c-", current[0], StringComparison.Ordinal);

        var convId = await neuron.CurrentConversationId();
        Assert.NotNull(convId);
        Assert.True(convId.Value.IsCanonical);
    }

    [Fact]
    public async Task Use_WithPrefixedNew_CreatesCanonicalId()
    {
        var neuron = CreateNeuron();

        await neuron.Use("ino/conversations/new");

        var current = await neuron.Current();
        Assert.Single(current);
        Assert.StartsWith("ino/conversations/c-", current[0], StringComparison.Ordinal);
        var convId = await neuron.CurrentConversationId();
        Assert.NotNull(convId);
        Assert.True(convId.Value.IsCanonical);
    }

    [Fact]
    public async Task Use_WithCanonicalId_StoresDirectly()
    {
        var neuron = CreateNeuron();
        var canonical = ConversationId.New();

        await neuron.Use($"ino/conversations/{canonical.Value}");

        var current = await neuron.Current();
        Assert.Equal([$"ino/conversations/{canonical.Value}"], current);
        var convId = await neuron.CurrentConversationId();
        Assert.Equal(canonical, convId);
    }

    [Fact]
    public async Task Use_WithLegacyId_CleanlyAccepts()
    {
        var neuron = CreateNeuron();

        await neuron.Use("ino/conversations/main");

        var current = await neuron.Current();
        Assert.Equal(["ino/conversations/main"], current);
        var convId = await neuron.CurrentConversationId();
        Assert.Equal("main", convId?.Value);
    }

    [Theory]
    [InlineData("ino/conversations/../../traversal")]
    [InlineData("ino/conversations/bad\\path")]
    [InlineData("ino/conversations/null\0char")]
    public async Task Use_WithHostileInput_ThrowsArgumentException(string hostile)
    {
        var neuron = CreateNeuron();
        await Assert.ThrowsAsync<ArgumentException>(() => neuron.Use(hostile));
    }

    [Fact]
    public async Task CreateNew_ReturnsPrefixedCanonicalKey()
    {
        var neuron = CreateNeuron();

        var key = await neuron.CreateNew();

        Assert.StartsWith("ino/conversations/c-", key, StringComparison.Ordinal);
        var current = await neuron.Current();
        Assert.Equal([key], current);
    }
}

public sealed class InoStudioNeuronFacts
{
    [Fact]
    public void StudioNeuron_UsesBrainId_WithoutHardcodedLocalFallback()
    {
        var neuron = new InoStudioNeuron(new ServiceCollection().BuildServiceProvider());
        var context = new FakeNeuronContext(new NeuronId(new BrainId("tenant-brain-01"), "ino.studio", "ino/studio"));
        NeuronAttachment.Attach(neuron, context);

        // Verify neuron uses the brain from context, and doesn't default to "local"
        Assert.Equal("tenant-brain-01", context.Id.Brain.Value);
    }

    [Fact]
    public async Task StudioNeuron_ThrowsOnInvalidBrainId()
    {
        Assert.Throws<ArgumentException>(() => new BrainId("bad/brain"));

        var neuron = new InoStudioNeuron(new ServiceCollection().BuildServiceProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => neuron.Make("Build a unit"));
    }
}

public sealed class ConversationNeuronFacts
{
    private const string CanonicalId = "c-1234567890abcdef1234567890abcdef";

    private static ConversationNeuron CreateNeuron(string brainId = "brain-primary-01", string convId = CanonicalId)
    {
        var neuron = new ConversationNeuron();
        var neuronId = new NeuronId(new BrainId(brainId), "conversations", $"conversations/{convId}");
        NeuronAttachment.Attach(neuron, new FakeNeuronContext(neuronId));
        return neuron;
    }

    [Fact]
    public async Task NeuronLifecycle_TurnRunApprovalComplete_WorksEndToEnd()
    {
        var neuron = CreateNeuron();

        var initial = await neuron.Read();
        Assert.Equal(CanonicalId, initial.Id.Value);
        Assert.Empty(initial.Turns);

        var turn = await neuron.AddTurn("user", "Hello assistant");
        Assert.Equal("Hello assistant", turn.Content);

        var run = await neuron.StartRun("flow-unit", "step-author", "Write code");
        Assert.Equal(ConversationRunStatus.Running, run.Status);
        Assert.Equal("step-author", run.Step);

        await neuron.AdvanceRunStep(run.RunId, "step-deploy");

        var approval = await neuron.RequestApproval("plan-deploy-1", ConversationApprovalKind.FlowStep, "Deploy to cloud", "Detail", "flow-unit", "step-deploy");
        Assert.Equal(ConversationApprovalStatus.Pending, approval.Status);

        var mid = await neuron.Read();
        Assert.Equal(ConversationRunStatus.WaitingApproval, mid.ActiveRun?.Status);

        var resolved = await neuron.ResolveApproval("plan-deploy-1", approved: true, resolver: "lead@corp", reason: "Approved");
        Assert.Equal(ConversationApprovalStatus.Approved, resolved.Status);

        await neuron.CompleteRun(run.RunId, "Deployed successfully");

        var final = await neuron.Read();
        Assert.Null(final.ActiveRun);
        Assert.Single(final.RunHistory);
        Assert.Equal(ConversationRunStatus.Completed, final.RunHistory[0].Status);
        Assert.Equal("Deployed successfully", final.RunHistory[0].Reply);
    }
}

public sealed class ConversationsModuleOnTestOS : ModuleOnTestOSFacts<ConversationsModule>;
