using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Capabilities.Storage.Gating;
using DigitalBrain.Confirmation;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// Regression coverage for WindowsNeuron write gating through StorageWriteGate (fail-closed, no fail-open bypass).
public sealed class WindowsWriteGateFacts : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BrainId Brain = new("alice");
    private static readonly MachineId Machine = new("pc-test-01");
    private static NeuronId WindowsNeuronId => ConnectionKeys.IdFor(ConnectionKeys.For(Brain.Value, "windows", Machine.Value));

    private readonly string _root = PathConfinement.Normalize(Directory.CreateTempSubdirectory("windows-write-gate-").FullName);

    [Fact]
    public async Task WriteDeniedWhenConfirmationIsDeniedAndSucceedsWhenApproved()
    {
        await using var os = Host();
        os.Confirmations.Policy = ConfirmationPolicy.Always;

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            await os.Get<IWindows>(WindowsNeuronId).AllowRoot(_root);
            var storage = os.Get<IStorage>(WindowsNeuronId);
            var root = Assert.Single(await storage.Roots(Ct));

            // Denied confirmation fails closed and leaves no file.
            var deniedWrite = storage.Write(WritePlan.WriteFile(root.Id, "blocked.txt", Encoding.UTF8.GetBytes("nope"), ConflictPolicy.Fail), Ct);
            await Eventually(() => Task.FromResult(os.Confirmations.Waiting.Count == 1));
            await os.Confirmations.Deny(os.Confirmations.Waiting[0].PlanId);
            await Assert.ThrowsAnyAsync<Exception>(() => deniedWrite);
            Assert.False(File.Exists(Path.Combine(_root, "blocked.txt")));

            // Approved confirmation allows the write.
            var approvedWrite = storage.Write(WritePlan.WriteFile(root.Id, "allowed.txt", Encoding.UTF8.GetBytes("ok"), ConflictPolicy.Fail), Ct);
            await Eventually(() => Task.FromResult(os.Confirmations.Waiting.Count == 1));
            await os.Confirmations.Approve(os.Confirmations.Waiting[0].PlanId);
            _ = await approvedWrite;
            await Eventually(() => Task.FromResult(File.Exists(Path.Combine(_root, "allowed.txt"))));
        }
    }

    [Fact]
    public async Task DryRunAndApprovedRunnerPermitBypassConfirmationPrompt()
    {
        var authorizer = new AllowNextAuthorizer();
        await using var os = Host(authorizer);
        os.Confirmations.Policy = ConfirmationPolicy.Always;

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            await os.Get<IWindows>(WindowsNeuronId).AllowRoot(_root);
            var storage = os.Get<IStorage>(WindowsNeuronId);
            var root = Assert.Single(await storage.Roots(Ct));

            // DryRun does not prompt.
            var dryRun = WritePlan.WriteFile(root.Id, "preview.txt", Encoding.UTF8.GetBytes("preview"), ConflictPolicy.Fail) with { DryRun = true };
            _ = await storage.Write(dryRun, Ct);
            Assert.Empty(os.Confirmations.Requested);
            Assert.Equal(1, authorizer.Consumes);
            Assert.False(File.Exists(Path.Combine(_root, "preview.txt")));

            // Approved runner permit allows the write without prompting.
            authorizer.AllowNext = true;
            _ = await storage.Write(WritePlan.WriteFile(root.Id, "permitted.txt", Encoding.UTF8.GetBytes("yes"), ConflictPolicy.Fail), Ct);
            Assert.Empty(os.Confirmations.Requested);
            Assert.Equal(2, authorizer.Consumes);
            await Eventually(() => Task.FromResult(File.Exists(Path.Combine(_root, "permitted.txt"))));
        }
    }

    [Fact]
    public async Task DirectWindowsNeuronFailsClosedWhenConfirmationsAreNull()
    {
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(_root)], new InMemoryOperations(), DriveVolumeSource.Instance, new ConfinedDiskCoordinator(), ConfinedDiskShape.Windows);
        var root = Assert.Single(await storage.Roots(Ct));
        var plan = WritePlan.WriteFile(root.Id, "fail-closed.txt", Encoding.UTF8.GetBytes("blocked"), ConflictPolicy.Fail);

        // WindowsNeuron delegates directly to StorageWriteGate.AuthorizeAsync with AlwaysConfirm;
        // missing confirmations must throw Denied rather than returning early (the old fail-open bug).
        await Assert.ThrowsAsync<Denied>(() => StorageWriteGate.AuthorizeAsync(
            WindowsNeuronId,
            storage,
            plan,
            caller: null,
            authorizer: null,
            confirmations: null,
            policy: StorageWritePolicy.AlwaysConfirm,
            ct: Ct));
        Assert.False(File.Exists(Path.Combine(_root, "fail-closed.txt")));
    }

    public void Dispose() => TempDirectory.DeleteIfPresent(_root);

    private static TestOS Host(IStorageWriteAuthorizer? authorizer = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Machine)
            .AddSingleton(new ConfinedDiskCoordinator())
            .AddSingleton<IOperations>(new InMemoryOperations());
        if (authorizer is not null)
        {
            services.AddSingleton(authorizer);
        }
        return TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<WindowsNeuron>();
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) { return; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The condition did not hold within five seconds.");
    }

    private sealed class AllowNextAuthorizer : IStorageWriteAuthorizer
    {
        public bool AllowNext { get; set; }
        public int Consumes { get; private set; }

        public IDisposable BeginApproved(BrainId brain, string requester) => throw new NotSupportedException();
        public Task<T> Invoke<T>(string connection, WritePlan primitive, Func<Task<T>> send, CancellationToken ct) => send();
        public Task<bool> Consume(NeuronId storageNeuron, WritePlan primitive, CancellationToken ct)
        {
            Consumes++;
            var allow = AllowNext;
            AllowNext = false;
            return Task.FromResult(allow);
        }
    }
}
