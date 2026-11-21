using System.Text;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class ArtifactLiveHubFacts
{
    [Fact]
    public async Task WriteBumpsVersionPersistsBytesAndNotifiesLiveSseSubscribers()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"artifact-hub-test-{Guid.NewGuid():N}");
        try
        {
            var hub = new ArtifactLiveHub(tempDir);
            var (subId, reader) = hub.Subscribe("/work/portrait.psd");

            var v1Bytes = Encoding.UTF8.GetBytes("PSD-v1-initial");
            var m1 = await hub.WriteAsync("/work/portrait.psd", v1Bytes, actor: "user", ct: ct);
            Assert.Equal(1, m1.Version);
            Assert.Equal("user", m1.UpdatedBy);
            Assert.Equal("image/vnd.adobe.photoshop", m1.MimeType);

            var evt1 = await reader.ReadAsync(ct);
            Assert.Equal(1, evt1.Version);
            Assert.Equal("user", evt1.UpdatedBy);

            // Ino edits the exact same artifact via MCP -> Version bumps to 2 and pushes live event.
            var v2Bytes = Encoding.UTF8.GetBytes("PSD-v2-nobg-by-ino");
            var m2 = await hub.WriteAsync("/work/portrait.psd", v2Bytes, actor: "ino", ct: ct);
            Assert.Equal(2, m2.Version);
            Assert.Equal("ino", m2.UpdatedBy);
            Assert.NotEqual(m1.Sha256, m2.Sha256);

            var evt2 = await reader.ReadAsync(ct);
            Assert.Equal(2, evt2.Version);
            Assert.Equal("ino", evt2.UpdatedBy);

            var readBack = await hub.ReadAsync("/work/portrait.psd", ct);
            Assert.NotNull(readBack);
            Assert.Equal(v2Bytes, readBack.Value.Bytes);

            hub.Unsubscribe("/work/portrait.psd", subId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }
}
