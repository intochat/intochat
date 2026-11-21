using System.Text.Json;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Link;

namespace IntoChat.OS.Modules.Devices.Link.Tests.Unit;

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

        Assert.Equal("/circle/v1/models", DeviceRoutes.Models);
        Assert.Equal("/circle/v1/compute/next", DeviceRoutes.Next);
        Assert.Equal("/circle/v1/compute/{jobId}/stream", DeviceRoutes.Stream);
        Assert.Equal("/circle/v1/compute/j%201/stream", DeviceRoutes.StreamFor("j 1"));
        Assert.Equal(25, DeviceRoutes.WaitSeconds);
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
        var json = JsonSerializer.Serialize(response, DeviceWireJson.Default.EnrollResponse);
        Assert.Contains("\"machineCredentialId\":\"m1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pendingAccount\":true", json, StringComparison.Ordinal);
        Assert.Equal(response, JsonSerializer.Deserialize(json, DeviceWireJson.Default.EnrollResponse));
    }

    [Theory]
    [InlineData(EnrollErrorKind.Rejected, "rejected")]
    [InlineData(EnrollErrorKind.Expired, "expired")]
    [InlineData(EnrollErrorKind.RateLimited, "rate_limited")]
    public void EnrollErrorKindsAreSnakeCaseStrings(EnrollErrorKind kind, string wire)
    {
        var json = JsonSerializer.Serialize(new EnrollError(kind), DeviceWireJson.Default.EnrollError);
        Assert.Equal($$"""{"kind":"{{wire}}"}""", json);
        Assert.Equal(kind, JsonSerializer.Deserialize(json, DeviceWireJson.Default.EnrollError)!.Kind);
    }

    [Fact]
    public void RequestsAndStatusRoundTrip()
    {
        var enroll = new EnrollRequest("CODE-1", "spki", "machine", "Desk", "Windows 11");
        Assert.Equal(enroll, JsonSerializer.Deserialize(JsonSerializer.Serialize(enroll, DeviceWireJson.Default.EnrollRequest), DeviceWireJson.Default.EnrollRequest));
        Assert.Equal("""{"refreshToken":"rt"}""", JsonSerializer.Serialize(new RefreshRequest("rt"), DeviceWireJson.Default.RefreshRequest));
        var status = new CircleStatusResponse("c1", "Home", "owner", PendingAccount: false, Revoked: false, "m1");
        Assert.Equal(status, JsonSerializer.Deserialize(JsonSerializer.Serialize(status, DeviceWireJson.Default.CircleStatusResponse), DeviceWireJson.Default.CircleStatusResponse));
    }

    [Fact]
    public void ADeviceChatJobRoundTripsThroughTheContext()
    {
        var job = new DeviceChatJob(
            "job-1",
            "qwen2.5-coder:14b",
            [new DeviceMessage("system", "Be brief."), new DeviceMessage("user", "Say hi.")],
            0.2f,
            256,
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        var json = JsonSerializer.Serialize(job, DeviceWireJson.Default.DeviceChatJob);
        var back = JsonSerializer.Deserialize(json, DeviceWireJson.Default.DeviceChatJob);

        Assert.NotNull(back);
        Assert.Equal((job.JobId, job.Model, job.Temperature, job.MaxOutputTokens, job.TraceParent),
            (back.JobId, back.Model, back.Temperature, back.MaxOutputTokens, back.TraceParent));
        Assert.Equal(job.Messages, back.Messages);

        var report = new ModelsReport("vlad-pc", [new AdvertisedModel("qwen2.5-coder:14b", "Qwen 2.5 Coder 14B", "qwen", Abilities.Text | Abilities.Tools)]);
        var reportBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(report, DeviceWireJson.Default.ModelsReport), DeviceWireJson.Default.ModelsReport);
        Assert.NotNull(reportBack);
        Assert.Equal(report.DeviceName, reportBack.DeviceName);
        Assert.Equal(report.Models, reportBack.Models);
    }

    [Fact]
    public void ChunksAreOneLineEach()
    {
        DeviceChunk[] chunks =
        [
            new("hello\nworld\r\nline 2"),
            new(null, Done: true, InputTokens: 12, OutputTokens: 34),
            new(null, Done: true, Error: "not-ready"),
        ];

        foreach (var chunk in chunks)
        {
            var json = JsonSerializer.Serialize(chunk, DeviceWireJson.Default.DeviceChunk);
            Assert.DoesNotContain('\n', json);
            Assert.DoesNotContain('\r', json);
            Assert.Equal(chunk, JsonSerializer.Deserialize(json, DeviceWireJson.Default.DeviceChunk));
        }
    }
}
