using System.Text;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class ArtifactLiveHubFacts
{
    [Fact]
    public async Task TheSamePathInTwoScopesIsTwoArtifacts()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), $"artifact-hub-test-{Guid.NewGuid():N}");
        try
        {
            var hub = new ArtifactLiveHub(tempDir);
            var (subId, alices) = hub.Subscribe("/work/note.txt", "alice");

            await hub.WriteAsync("/work/note.txt", Encoding.UTF8.GetBytes("alice's"), ct: ct, scope: "alice");
            await hub.WriteAsync("/work/note.txt", Encoding.UTF8.GetBytes("bob's"), ct: ct, scope: "bob");
            await hub.WriteAsync("/work/note.txt", Encoding.UTF8.GetBytes("bob's again"), ct: ct, scope: "bob");

            Assert.Equal("alice's", Encoding.UTF8.GetString((await hub.ReadAsync("/work/note.txt", ct, "alice"))!.Value.Bytes));
            Assert.Equal("bob's again", Encoding.UTF8.GetString((await hub.ReadAsync("/work/note.txt", ct, "bob"))!.Value.Bytes));
            Assert.Equal(1, hub.TryGetMetadata("/work/note.txt", "alice")!.Version);
            Assert.Equal(2, hub.TryGetMetadata("/work/note.txt", "bob")!.Version);
            Assert.Null(hub.TryGetMetadata("/work/note.txt", "carol"));
            Assert.Null(hub.TryGetMetadata("/work/note.txt"));

            // Alice's subscription heard her own write and neither of Bob's.
            Assert.Equal(1, (await alices.ReadAsync(ct)).Version);
            Assert.False(alices.TryRead(out _));
            hub.Unsubscribe("/work/note.txt", subId, "alice");

            // A scope is one folder name: it cannot point a path somewhere else.
            foreach (var scope in new[] { "", "..", "a/b", "a\\b", "c:" })
            {
                await Assert.ThrowsAsync<ArgumentException>(() => hub.WriteAsync("/work/note.txt", [1], ct: ct, scope: scope));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); }
        }
    }

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
