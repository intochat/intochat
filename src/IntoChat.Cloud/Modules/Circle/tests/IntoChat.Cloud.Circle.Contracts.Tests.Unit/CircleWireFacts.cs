using System.Text.Json;

namespace IntoChat.Cloud.Circle.Tests.Unit;

public sealed class CircleWireFacts
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RoutesLiveUnderCircleV1()
    {
        Assert.Equal("/circle/v1/enroll", CircleRoutes.Enroll);
        Assert.Equal("/circle/v1/refresh", CircleRoutes.Refresh);
        Assert.Equal("/circle/v1/leave", CircleRoutes.Leave);
        Assert.Equal("/circle/v1/status", CircleRoutes.Status);
        Assert.Equal("/circle/v1/journal/{brainId}", CircleRoutes.Journal);
        Assert.Equal("/circle/v1/journal/{brainId}/snapshot", CircleRoutes.JournalSnapshot);
        Assert.Equal("/circle/v1/store", CircleRoutes.Store);
        Assert.Equal("/circle/v1/store/{publisher}/{name}/{version}", CircleRoutes.StorePackage);
        Assert.Equal("/circle/v1/files/{brainId}/{**path}", CircleRoutes.Files);
        Assert.Equal("X-Machine-Proof", CircleRoutes.MachineProofHeader);
    }

    [Fact]
    public void ConcreteRoutesEscapeEverySegment()
    {
        Assert.Equal("/circle/v1/journal/b%201", CircleRoutes.JournalFor("b 1"));
        Assert.Equal("/circle/v1/journal/b1/snapshot", CircleRoutes.JournalSnapshotFor("b1"));
        Assert.Equal("/circle/v1/store/acme/notes/1.0.0", CircleRoutes.StorePackageFor("acme", "notes", "1.0.0"));
        Assert.Equal("/circle/v1/files/b1/docs/a%20b.txt", CircleRoutes.FilesFor("b1", "/docs//a b.txt"));
    }

    [Fact]
    public void EnrollResponseRoundTripsInCamelCase()
    {
        var response = new EnrollResponse("m1", "at", At, "rt", At.AddDays(30), new CircleInfo("c1", "Friends", "member"), PendingAccount: true,
            new CircleEndpoints("j", "s", "f"));
        var json = JsonSerializer.Serialize(response, CircleJsonContext.Default.EnrollResponse);
        Assert.Contains("\"machineCredentialId\":\"m1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pendingAccount\":true", json, StringComparison.Ordinal);
        Assert.Equal(response, JsonSerializer.Deserialize(json, CircleJsonContext.Default.EnrollResponse));
    }

    [Theory]
    [InlineData(EnrollErrorKind.Rejected, "rejected")]
    [InlineData(EnrollErrorKind.Expired, "expired")]
    [InlineData(EnrollErrorKind.RateLimited, "rate_limited")]
    public void EnrollErrorKindsAreSnakeCaseStrings(EnrollErrorKind kind, string wire)
    {
        var json = JsonSerializer.Serialize(new EnrollError(kind), CircleJsonContext.Default.EnrollError);
        Assert.Equal($$"""{"kind":"{{wire}}"}""", json);
        Assert.Equal(kind, JsonSerializer.Deserialize(json, CircleJsonContext.Default.EnrollError)!.Kind);
    }

    [Fact]
    public void JournalShapesRoundTrip()
    {
        var batch = new JournalBatch(4, 9, 5, "nonce", "cipher", "k1");
        var json = JsonSerializer.Serialize(batch, CircleJsonContext.Default.JournalBatch);
        Assert.Equal("""{"fromExclusive":4,"lastSignal":9,"count":5,"nonce":"nonce","ciphertext":"cipher","keyId":"k1"}""", json);
        Assert.Equal(batch, JsonSerializer.Deserialize(json, CircleJsonContext.Default.JournalBatch));
        Assert.Equal(new JournalAck(9), JsonSerializer.Deserialize("""{"highWater":9}""", CircleJsonContext.Default.JournalAck));
        Assert.Equal(new JournalConflict(3), JsonSerializer.Deserialize("""{"highWater":3}""", CircleJsonContext.Default.JournalConflict));
        var snapshot = new SnapshotUpload(9, "n", "c", "k1");
        Assert.Equal(snapshot, JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, CircleJsonContext.Default.SnapshotUpload), CircleJsonContext.Default.SnapshotUpload));
    }

    [Fact]
    public void RequestsRoundTrip()
    {
        var enroll = new EnrollRequest("CODE-1", "spki", "machine", "Desk", "Windows 11");
        Assert.Equal(enroll, JsonSerializer.Deserialize(JsonSerializer.Serialize(enroll, CircleJsonContext.Default.EnrollRequest), CircleJsonContext.Default.EnrollRequest));
        Assert.Equal("""{"refreshToken":"rt"}""", JsonSerializer.Serialize(new RefreshRequest("rt"), CircleJsonContext.Default.RefreshRequest));
    }

    [Fact]
    public void StorePageRoundTrips()
    {
        var page = new StorePage([new CloudListing("acme", "notes", "1.0.0", "sha256:ab", 42, "Notes", ["files@1"], At)], "next");
        var json = JsonSerializer.Serialize(page, CircleJsonContext.Default.StorePage);
        Assert.Contains("\"requiresCapabilities\":[\"files@1\"]", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, CircleJsonContext.Default.StorePage)!;
        Assert.Equal("next", back.NextCursor);
        Assert.Equal(page.Items[0] with { RequiresCapabilities = back.Items[0].RequiresCapabilities }, back.Items[0]);
        Assert.Equal(["files@1"], back.Items[0].RequiresCapabilities);
    }
}
