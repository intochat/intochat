using System.Net;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// R40: the owner of a part's brain releases it and reads its records over HTTP; anyone else is refused, and a read
// of a part this machine does not hold is a conflict, never an implicit acquire.
public sealed class MachinePartEndpointFacts
{
    [Fact(Timeout = 120_000)]
    public async Task TheOwnerReleasesAPartAndReadsItsRecordsAndOthersAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().WithHttpEdge().StartAsync(ct);
        var scope = BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;
        var key = $"{scope}/apps/counter/c1";
        var part = PartId.Of(key);
        var logs = brain.SiloServices.GetRequiredService<PartLogs>();
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        await (await logs.For(part)).Append($"counter/{key}", "counter", RecordChange.Write, serializer.SerializeToArray(3), []);
        var http = brain.HttpClient;
        string Path(string what) => $"/machine/parts/{Uri.EscapeDataString(part.Value)}/{what}";

        var read = await http.GetAsync(Path($"records/{Uri.EscapeDataString(key)}"), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var record = Assert.Single(JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct)).RootElement.EnumerateArray());
        Assert.Equal(3, serializer.Deserialize<int>(Convert.FromBase64String(record.GetProperty("record").GetString()!)));

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(Path("release"), null, ct)).StatusCode);
        Assert.DoesNotContain(part, logs.Opened);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(Path($"records/{Uri.EscapeDataString(key)}"), ct)).StatusCode);
        Assert.DoesNotContain(part, logs.Opened);

        await brain.OwnBrainAsync("alice", "alice-home");
        var theirs = Uri.EscapeDataString(PartId.ForApp(BrainScope.Create("alice", "alice-home").Id, "counter").Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"/machine/parts/{theirs}/release", null, ct)).StatusCode);
    }
}
