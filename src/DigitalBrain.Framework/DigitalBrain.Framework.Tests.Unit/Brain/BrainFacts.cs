using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework.Tests.Unit.Brain;

public sealed class BrainFacts
{
    [Fact]
    public async Task CreateIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        var kernel = host.Get<IKernel>(BrainScope.Create("acct", "personal").Id);
        await kernel.Create(new Owner("acct"));
        var first = await kernel.Read();
        await kernel.Create(new Owner("acct"));
        var second = await kernel.Read();
        Assert.Equal("acct", first.Owner.AccountId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
    }

    // Activated arrives during the grain's own activation, before any per-activation watch can
    // re-attach, so these facts listen where the operating system does: the silo's signal hub.
    [Fact]
    public async Task ACreatedBrainPublishesActivatedOnBirthAndOnEveryLaterWake()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        using var activations = host.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Activated>();
        var scope = BrainScope.Create("acct", "waking").Id;
        var kernel = host.Get<IKernel>(scope);
        await kernel.Create(new Owner("acct"));
        var born = await activations.Reader.ReadAsync(ct);
        Assert.Equal("acct", born.Owner.AccountId);
        Assert.EndsWith("/" + scope, born.Publisher.Name, StringComparison.Ordinal);
        await host.DeactivateAsync(kernel, ct);
        await kernel.Read();
        Assert.EndsWith("/" + scope, (await activations.Reader.ReadAsync(ct)).Publisher.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUncreatedBrainStaysSilentOnActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        using var activations = host.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Activated>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Get<IKernel>(BrainScope.Create("acct", "silent").Id).Read());
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await activations.Reader.ReadAsync(quiet.Token));
    }
}
