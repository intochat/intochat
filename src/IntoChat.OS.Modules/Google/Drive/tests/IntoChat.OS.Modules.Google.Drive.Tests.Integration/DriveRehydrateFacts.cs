using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// The Drive grain moved assemblies in the Google split; its grain type, state name and state alias did not,
// so a connection persisted before a restart reactivates from the file grain store and still lists its root.
public sealed class DriveRehydrateFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task APersistedDriveConnectionReactivatesAfterARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var drive = await DriveStub.StartAsync(ct);
        await using var brain = await DriveBrain.StartAsync(drive, ct);
        var key = ConnectionKeys.For(brain.Owner.BrainId, "google-drive", "primary");
        using (brain.Owner.AsCaller())
        {
            await brain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "drive-token", "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1), DriveScopes.Read, null, "{}"));
        }

        await brain.RestartAsync(ct);

        using var caller = brain.Owner.AsCaller();
        var connection = Assert.Single(await brain.Get<IConnectionRequests>(IConnectionRequests.Key(brain.Owner.BrainId)).ListConnections(), item => item.Neuron == key);
        Assert.Equal("user@gmail.com", connection.Account);
        var top = await brain.Get<ILegacyStorage>(key).List("root", ct);
        Assert.Equal(["Notes", "Plan"], top.Select(entry => entry.Name));
        Assert.Equal("Bearer drive-token", drive.LastAuthorization);
    }
}
