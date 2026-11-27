using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using Xunit;

namespace DigitalBrain.Tests.Unit.Capabilities.Storage;

public sealed class StorageWriteGateFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BrainId Brain = new("alice");
    private static readonly NeuronId StorageNeuron = new(Brain, "local-cloud", "main");

    [Fact]
    public async Task AlwaysConfirmFailsClosedWhenConfirmationsAreMissing()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs");

        await Assert.ThrowsAsync<Denied>(() => StorageWriteGate.AuthorizeAsync(
            StorageNeuron,
            storage,
            plan,
            os.Edge.Person("alice", "alice", Brain),
            authorizer: null,
            confirmations: null,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct));
    }

    [Fact]
    public async Task AlwaysConfirmDeniesWhenConfirmationIsNotApproved()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs");
        var confirmations = new StubConfirmations(ConfirmationOutcome.Denied);

        await Assert.ThrowsAsync<Denied>(() => StorageWriteGate.AuthorizeAsync(
            StorageNeuron,
            storage,
            plan,
            os.Edge.Person("alice", "alice", Brain),
            authorizer: null,
            confirmations: confirmations,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct));
        Assert.Equal(1, confirmations.Calls);
    }

    [Fact]
    public async Task AlwaysConfirmAllowsWhenConfirmationIsApproved()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs");
        var confirmations = new StubConfirmations(ConfirmationOutcome.Approved);

        await StorageWriteGate.AuthorizeAsync(
            StorageNeuron,
            storage,
            plan,
            os.Edge.App("app.files", new Actor.Person("alice", "alice"), Brain),
            authorizer: null,
            confirmations: confirmations,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct);
        Assert.Equal(1, confirmations.Calls);
    }

    [Fact]
    public async Task DryRunBypassesConfirmationEvenWhenConfirmationsAreMissing()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs") with { DryRun = true };

        await StorageWriteGate.AuthorizeAsync(
            StorageNeuron,
            storage,
            plan,
            os.Edge.App("app.files", new Actor.Person("alice", "alice"), Brain),
            authorizer: null,
            confirmations: null,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct);
    }

    [Fact]
    public async Task ApprovedRunnerPermitBypassesConfirmation()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs");
        var authorizer = new StubAuthorizer(allow: true);
        var confirmations = new StubConfirmations(ConfirmationOutcome.Denied);

        await StorageWriteGate.AuthorizeAsync(
            StorageNeuron,
            storage,
            plan,
            os.Edge.App("app.files", new Actor.Person("alice", "alice"), Brain),
            authorizer: authorizer,
            confirmations: confirmations,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct);

        Assert.Equal(1, authorizer.Consumes);
        Assert.Equal(0, confirmations.Calls);
    }

    [Fact]
    public async Task WorkspaceDirectAllowsDirectSameBrainPersonModuleAndKernelOnly()
    {
        await using var os = TestOS.Local();
        var storage = new InMemoryStorage();
        var plan = WritePlan.CreateFolder(storage.Root, "docs");
        var alice = new Actor.Person("alice", "alice");

        // Allowed without confirmation under WorkspaceDirect: direct Person, Module, Kernel of same brain.
        await StorageWriteGate.AuthorizeAsync(StorageNeuron, storage, plan, os.Edge.Person("alice", "alice", Brain), null, null, StorageWritePolicy.WorkspaceDirect, Ct);
        await StorageWriteGate.AuthorizeAsync(StorageNeuron, storage, plan, os.Edge.Module("files", Brain), null, null, StorageWritePolicy.WorkspaceDirect, Ct);
        await StorageWriteGate.AuthorizeAsync(StorageNeuron, storage, plan, os.Edge.Kernel(Brain), null, null, StorageWritePolicy.WorkspaceDirect, Ct);

        // Denied without confirmation under WorkspaceDirect: App, Agent, relayed Person, cross-brain Person/Module/Kernel, null caller.
        Caller?[] mustConfirm =
        [
            os.Edge.App("app.files", alice, Brain),
            os.Edge.Agent(alice, Brain),
            os.Edge.RelayedPerson("alice", "alice", Brain, inside: true),
            os.Edge.Person("bob", "bob", new BrainId("bob")),
            os.Edge.Module("files", new BrainId("bob")),
            os.Edge.Kernel(new BrainId("bob")),
            null,
        ];

        foreach (var caller in mustConfirm)
        {
            await Assert.ThrowsAsync<Denied>(() => StorageWriteGate.AuthorizeAsync(
                StorageNeuron,
                storage,
                plan,
                caller,
                authorizer: null,
                confirmations: null,
                policy: StorageWritePolicy.WorkspaceDirect,
                ct: Ct));
        }
    }

    private sealed class StubConfirmations(ConfirmationOutcome outcome) : IConfirmations
    {
        public int Calls { get; private set; }
        public ConfirmationPolicy Policy => ConfirmationPolicy.Always;

        public Task<ConfirmationResult> Confirm(ConfirmationPlan plan, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ConfirmationResult(plan.Id, outcome, ConfirmationDenyReason.None, false));
        }

        public Task Resolve(string planId, bool approve, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubAuthorizer(bool allow) : IStorageWriteAuthorizer
    {
        public int Consumes { get; private set; }

        public IDisposable BeginApproved(BrainId brain, string requester) => throw new NotSupportedException();
        public Task<T> Invoke<T>(string connection, WritePlan primitive, Func<Task<T>> send, CancellationToken ct) => send();
        public Task<bool> Consume(NeuronId storageNeuron, WritePlan primitive, CancellationToken ct)
        {
            Consumes++;
            return Task.FromResult(allow);
        }
    }
}
