using System.Threading.Channels;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

// The brain neuron is a plain neuron of the kernel composition (06-08), keyed {brain}/main.
public sealed class BrainFacts(OrleansHost host)
{
    [Fact]
    public async Task ABrainKeepsItsBirthAcrossEveryLaterRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var alice = await host.PersonAsync("alice", ct);
        var self = brain.Get<IBrain>(IBrain.Key(alice.BrainId));
        State born;
        using (alice.AsCaller()) { born = await self.Read(); }

        // Every allowed request to a brain asks for its creation again.
        using var again = await alice.Http.GetAsync($"/brains/{alice.BrainId}/connections", ct);
        again.EnsureSuccessStatusCode();

        using var caller = alice.AsCaller();
        var later = await self.Read();
        Assert.Equal(alice.AccountId, born.Owner.AccountId);
        Assert.Equal(born.CreatedAt, later.CreatedAt);
    }

    // Activated is published in the neuron's own activation, before a synapse could attach, so these facts listen where the
    // operating system does (FeatureBoot): the silo's cluster listeners.
    [Fact]
    public async Task ABornBrainPublishesActivatedOnBirthAndOnEveryLaterWake()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var activations = await Listen(brain, nameof(ABornBrainPublishesActivatedOnBirthAndOnEveryLaterWake));
        var alice = await host.PersonAsync("alice", ct);
        var born = await NextFor(activations, alice.BrainId, ct);
        Assert.Equal(alice.AccountId, born.Owner.AccountId);

        var self = brain.Get<IBrain>(IBrain.Key(alice.BrainId));
        await brain.DeactivateAsync(self, ct);
        using (alice.AsCaller()) { await self.Read(); }

        await NextFor(activations, alice.BrainId, ct);
    }

    [Fact]
    public async Task AnUnbornBrainStaysSilentOnActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var activations = await Listen(brain, nameof(AnUnbornBrainStaysSilentOnActivation));
        var silent = BrainScope.Create("acct", "silent").Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IBrain>(IBrain.Key(silent)).Read());
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await NextFor(activations, silent, quiet.Token));
    }

    [Fact]
    public async Task AStampedCallerCannotAnnounceForTheKernel()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.PersonAsync("alice", ct);
        var announcer = SiloNeurons.Get<IntoChat.OS.Kernel.Composition.IBrainAnnouncer>(host.Brain.Services, IBrain.Key(alice.BrainId));
        var created = await Listen<InstanceCreated>(host.Brain, nameof(AStampedCallerCannotAnnounceForTheKernel));
        using (alice.AsCaller()) { await announcer.InstanceCreated("forged", alice.BrainId + "/forged"); }
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (true)
            {
                var (_, signal) = await created.ReadAsync(quiet.Token);
                if (signal.Type == "forged") { return; }
            }
        });
    }

    private static async Task<ChannelReader<(DigitalBrain.NeuronId Publisher, Activated Signal)>> Listen(ITestBrain brain, string name)
        => await Listen<Activated>(brain, name);

    private static async Task<ChannelReader<(DigitalBrain.NeuronId Publisher, T Signal)>> Listen<T>(ITestBrain brain, string name) where T : DigitalBrain.Signal
    {
        var channel = Channel.CreateUnbounded<(DigitalBrain.NeuronId, T)>();
        await brain.Services.GetRequiredService<DigitalBrain.Spi.IClusterListeners>().Listen<T>("brain-facts-" + name, (publisher, signal, _) =>
        {
            channel.Writer.TryWrite((publisher, signal));
            return Task.CompletedTask;
        });
        return channel.Reader;
    }

    private static async Task<Activated> NextFor(ChannelReader<(DigitalBrain.NeuronId Publisher, Activated Signal)> activations, string brainId, CancellationToken ct)
    {
        while (true)
        {
            var (publisher, activated) = await activations.ReadAsync(ct);
            if (publisher.Brain.Value == brainId && publisher.Type == IBrain.Type) { return activated; }
        }
    }
}
