using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// Google Drive through the storage capability, against the Drive stub.
public sealed class DriveStorageFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AConnectedDriveIsDiscoveredAsStorageAndBrowsedWithItsToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var drive = await DriveStub.StartAsync(ct);
        await using var brain = await DriveBrain.StartAsync(drive, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, DriveModule.Connector.Id, "primary");
        using var caller = brain.Owner.AsCaller();
        var view = await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "drive-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), DriveScopes.Read, null, "{}"));
        Assert.Equal("user@gmail.com", view.Account);

        var connection = Assert.Single(await brain.Get<IConnectionRequests>(brain.Owner.BrainId).ListConnections(), item => item.Neuron == key);
        Assert.Contains(IStorage.Capability, connection.Capabilities);

        var storage = brain.Get<IStorage>(key);
        var root = Assert.Single(await storage.Roots(ct));
        var top = await storage.List(root.Location, ct);
        Assert.Equal([("Notes", true), ("Plan", false)], top.Select(entry => (entry.Name, entry.IsFolder)));
        var file = Assert.Single(await storage.List(top[0].Location, ct));
        Assert.Equal("hello from drive", await storage.Read(file.Location, ct));
        Assert.Equal("exported plan", await storage.Read(top[1].Location, ct));
        Assert.Equal(["My Drive", "Notes", "a.txt"], (await storage.Ancestors(file.Location, ct)).Select(entry => entry.Name));
        Assert.Equal("Bearer drive-token", drive.LastAuthorization);
        var page = (await brain.Owner.Http.GetFromJsonAsync<ComputeTracePage>($"/brains/{brain.Owner.BrainId}/compute/traces/{TraceContext.Current!.TraceId}", new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }, ct))!;
        Assert.Contains(page.Rows, cost => cost.Item == "google.drive.files.get.media");
        Assert.Contains(page.Rows, cost => cost.Item == "google.drive.files.export");
        Assert.Contains(page.Rows, cost => cost.Item == "google.drive.about.get");
        Assert.All(page.Rows, cost =>
        {
            Assert.Equal(CostKind.ExternalProvider, cost.Kind);
            Assert.Equal(key, cost.Neuron);
            Assert.Equal(FundingKind.OwnConnection, cost.FundingKind);
            Assert.Equal("request", cost.Unit);
            Assert.Equal(1, cost.Quantity);
            Assert.Null(cost.ListPriceMicros);
            Assert.Equal(0, cost.Micros);
        });
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AFolderIsNotReadAsText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var drive = await DriveStub.StartAsync(ct);
        await using var brain = await DriveBrain.StartAsync(drive, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, DriveModule.Connector.Id, "primary");
        using var caller = brain.Owner.AsCaller();
        await brain.Grains().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "drive-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), DriveScopes.Read, null, "{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<IStorage>(key).Read("notes", ct));
    }
}
