using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework.Tests.Unit.Brain;

public sealed class BrainFacts
{
    [Fact]
    public async Task ABrainKeepsItsBirthAcrossEveryLaterRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var alice = await brain.PersonAsync("alice", ct);
        var self = brain.Get<IBrain>(alice.BrainId);
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

    // Activated arrives during the grain's own activation, before any per-activation watch can
    // re-attach, so these facts listen where the operating system does: the silo's signal hub.
    [Fact]
    public async Task ABornBrainPublishesActivatedOnBirthAndOnEveryLaterWake()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var activations = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<Activated>();
        var alice = await brain.PersonAsync("alice", ct);
        var born = await NextFor(activations, alice.BrainId, ct);
        Assert.Equal(alice.AccountId, born.Owner.AccountId);

        var self = brain.Get<IBrain>(alice.BrainId);
        await brain.DeactivateAsync(self, ct);
        using (alice.AsCaller()) { await self.Read(); }

        await NextFor(activations, alice.BrainId, ct);
    }

    [Fact]
    public async Task AnUnbornBrainStaysSilentOnActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var activations = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<Activated>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IBrain>(BrainScope.Create("acct", "silent").Id).Read());
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await NextFor(activations, "silent", quiet.Token));
    }

    private static async Task<Activated> NextFor(LocalSignalHub.Subscription<Activated> activations, string brainId, CancellationToken ct)
    {
        while (true)
        {
            var activated = await activations.Reader.ReadAsync(ct);
            if (activated.Publisher.Name.EndsWith("/" + brainId, StringComparison.Ordinal)) { return activated; }
        }
    }
}
