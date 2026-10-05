using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Framework.Tests.Unit.Enforcement;

// A brain someone uses is a brain the OS knows about: a development owner never registers, so
// the first allowed request establishes its brain, and its Activated starts the OS boot.
public sealed class BrainPresenceFacts
{
    [Fact]
    public async Task AnAllowedRequestEstablishesItsBrainSoItsActivationIsAnnounced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var activations = brain.SiloServices.GetRequiredService<LocalSignalHub>().Subscribe<Activated>();
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner", AccountId = "owner", BrainId = "owner",
            Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            var http = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IBrainAccess, AllowEveryone>()
                    .AddSingleton(brain.SiloServices.GetRequiredService<IDigitalBrain>())
                    .BuildServiceProvider(),
            };

            Assert.Null(await BrainAccessFilter.Decide(http, "owner"));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var activated = await activations.Reader.ReadAsync(deadline.Token);
            Assert.Equal(BrainScope.Create("owner", "owner").Id, activated.BrainId);
            Assert.Equal(("owner", "owner"), (activated.OwnerAccountId, activated.Name));
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    private sealed class AllowEveryone : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }
}
