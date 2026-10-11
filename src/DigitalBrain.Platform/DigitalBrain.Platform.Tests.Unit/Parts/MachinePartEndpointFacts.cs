using System.Net;
using System.Text.Json;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// R40: the owner of a part's brain releases it and reads its records over HTTP; anyone else is refused, and a read
// of a part this machine does not hold is a conflict, never an implicit acquire.
public sealed class MachinePartEndpointFacts
{
    [Fact]
    public async Task TheOwnerReleasesAPartAndReadsItsRecordsAndOthersAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PulseModule>().WithPartLog().StartAsync(ct);
        var owner = brain.Owner;
        var key = owner.Key("apps/counter/c1");
        var part = PartId.Of(key);
        using (owner.AsCaller()) { await brain.Get<IPulse>(key).Pulse(3); }
        var logs = brain.Services.GetRequiredService<PartLogs>();
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var http = owner.Http;
        string Path(string what) => $"/machine/parts/{Uri.EscapeDataString(part.Value)}/{what}";

        var read = await http.GetAsync(Path($"records/{Uri.EscapeDataString(key)}"), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var record = Assert.Single(JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct)).RootElement.EnumerateArray());
        Assert.Equal(3, serializer.Deserialize<PulseState>(Convert.FromBase64String(record.GetProperty("record").GetString()!))!.Last?.Value);

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(Path("release"), null, ct)).StatusCode);
        Assert.DoesNotContain(part, logs.Opened);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(Path($"records/{Uri.EscapeDataString(key)}"), ct)).StatusCode);
        Assert.DoesNotContain(part, logs.Opened);

        using var alice = await brain.PersonAsync("alice", ct);
        var theirs = Uri.EscapeDataString(PartId.ForApp(alice.BrainId, "counter").Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"/machine/parts/{theirs}/release", null, ct)).StatusCode);
    }
}
