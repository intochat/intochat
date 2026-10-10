using System.Text.Json;
using DigitalBrain.Circles;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using DigitalBrain.Testing.Module;

namespace DigitalBrain.Modules.Flutter.Tests.Workspace;

public sealed class BrainEventsFacts
{
    private static async Task<JsonDocument> Next(IAsyncEnumerator<string> stream, CancellationToken ct)
    {
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        return JsonDocument.Parse(stream.Current);
    }

    private static string[] Ids(JsonDocument snapshot)
        => [.. snapshot.RootElement.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()!)];

    [Fact]
    public async Task ANewNeuronAppearsInTheBrainStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CirclesModule>().StartAsync(ct);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "owner",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        var scope = BrainScope.CurrentId();
        await brain.Self.Create(new Owner("owner"));
        var grown = ICircle.Key(scope, "club");
        await using var stream = BrainEventsEndpoints.Snapshots(brain, scope, ct).GetAsyncEnumerator(ct);

        using var first = await Next(stream, ct);
        Assert.Equal(scope, first.RootElement.GetProperty("rootId").GetString());
        Assert.DoesNotContain(grown, Ids(first));

        await brain.Get<ICircle>(grown).Invite("bob");

        while (true)
        {
            using var next = await Next(stream, ct);
            if (Ids(next).Contains(grown)) { break; }
        }
    }
}
