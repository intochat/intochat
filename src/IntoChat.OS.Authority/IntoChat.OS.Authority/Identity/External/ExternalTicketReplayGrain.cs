using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Identity.External;

// Single use for external sign-in tickets: a jti is accepted once until its ticket expires. Keyed by issuer and a
// two-hex-digit shard of the jti, so one grain never holds more than its shard's live minute of tickets.
[Alias("identity.external-ticket-replay-grain")]
internal interface IExternalTicketReplay : IGrainWithStringKey
{
    static string Key(string issuer, string jti)
        => $"external-ticket/{issuer}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jti)))[..2]}";

    Task<bool> Consume(string jti, DateTimeOffset expiresAt);
}

[GrainType("identity.external-ticket-replay")]
internal sealed class ExternalTicketReplayGrain(
    [PersistentState("external-ticket-replay", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<ExternalTicketReplayState> store,
    TimeProvider clock) : Grain, IExternalTicketReplay
{
    public async Task<bool> Consume(string jti, DateTimeOffset expiresAt)
    {
        var now = clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(jti) || jti.Length > 128 || expiresAt <= now ||
            expiresAt > now.AddSeconds(IntoChat.OS.Framework.Modules.Identity.ExternalSignInTicket.LifetimeSeconds))
        { return false; }
        var live = store.State.Used.Where(entry => entry.ExpiresAt > now).ToArray();
        if (live.Any(entry => entry.Jti == jti)) { return false; }
        store.State.Used = [.. live, new ExternalTicketUse { Jti = jti, ExpiresAt = expiresAt }];
        await store.WriteStateAsync();
        return true;
    }
}

// Short-lived replay protection; never exported with a brain.
[GenerateSerializer, Alias("identity.external-ticket-replay-state")]
internal sealed class ExternalTicketReplayState
{
    [Id(0)] public ExternalTicketUse[] Used { get; set; } = [];
}

[GenerateSerializer, Alias("identity.external-ticket-use")]
internal sealed class ExternalTicketUse
{
    [Id(0)] public string Jti { get; set; } = "";
    [Id(1)] public DateTimeOffset ExpiresAt { get; set; }
}
