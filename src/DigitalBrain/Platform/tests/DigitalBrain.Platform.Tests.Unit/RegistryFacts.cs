using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class RegistryFacts
{
    [Fact]
    public async Task CensusReportsRealActivationsAcrossBrainsAndRefreshesAfterDeactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().StartAsync(ct);
        var first = host.Get<IBrain>(BrainScope.Create("owner", "one").Id);
        var second = host.Get<IBrain>(BrainScope.Create("owner", "two").Id);
        await first.Read();
        await second.Read();
        var census = host.SiloServices.GetRequiredService<IActivationCensus>();
        var type = first.GetGrainId().Type.ToString();
        var records = await census.Activations(type, ct);
        Assert.Contains(records, record => record.Key == first.GetGrainId().Key.ToString());
        Assert.Contains(records, record => record.Key == second.GetGrainId().Key.ToString());
        Assert.All(records, record => Assert.Equal(type, record.TypeId));
        await host.DeactivateAsync(first, ct);
        Assert.DoesNotContain(await census.Activations(type, ct), record => record.Key == first.GetGrainId().Key.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FederationPreservesHealthyDomainsAndNamesFailures(bool active)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ModuleTest.Create().ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<IRegistryInventoryProvider>(new Provider("first"));
            silo.Services.AddSingleton<IRegistryInventoryProvider>(new Provider("second"));
            silo.Services.AddSingleton<IRegistryInventoryProvider>(new Provider("broken", true));
        }).StartAsync(ct);
        var registry = host.SiloServices.GetRequiredService<IRegistry>();
        foreach (var brain in new[] { "one", "two" })
        {
            CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = brain,
                Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
            try
            {
                var result = active ? await registry.Active("test", ct) : await registry.Persisted("test", ct);
                Assert.Contains("first", result.Domains);
                Assert.Contains("second", result.Domains);
                Assert.Contains("broken", result.Domains);
                Assert.Equal(["first", "second"], result.Records.Select(record => record.Domain));
                Assert.All(result.Records, record => Assert.Equal(BrainScope.Create("owner", brain).Id, record.Key));
                Assert.All(result.Records, record => Assert.Equal(active ? "active" : "persisted", record.Display));
                Assert.Contains(result.Errors, error => error.Provider == "broken");
                Assert.DoesNotContain(result.Errors, error => error.Message.Contains("secret", StringComparison.Ordinal));
                var filtered = active ? await registry.Active("other", ct) : await registry.Persisted("other", ct);
                Assert.Empty(filtered.Records);
            }
            finally { RequestContext.Clear(); }
        }
        await Assert.ThrowsAsync<UntrustedCallerException>(() => registry.Persisted(ct: ct));
        await Assert.ThrowsAsync<UntrustedCallerException>(() => registry.Active(ct: ct));
    }

    private sealed class Provider(string domain, bool fails = false) : IRegistryInventoryProvider
    {
        public string Domain => domain;
        public Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct) => Read(typeId, "persisted");
        public Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct) => Read(typeId, "active");
        private Task<NeuronRecord[]> Read(string? typeId, string display)
        {
            if (fails) { throw new IOException("secret provider internals"); }
            return Task.FromResult<NeuronRecord[]>(typeId is null or "test"
                ? [new(Domain, "test", BrainScope.CurrentId(), display)] : []);
        }
    }
}
