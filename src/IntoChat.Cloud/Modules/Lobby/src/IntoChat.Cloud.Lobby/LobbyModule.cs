using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http;
using IntoChat.Cloud.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Lobby;

// One lobby brain per deployment. Visitors never hold Open posture and a lobby token is never promoted to a member session.
[Module(Id)]
public sealed class LobbyModule : Module
{
    public const string Id = "lobby";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";
    public const string TokenSecret = "lobby-token-key";

    // The plan's secret spelling auth-lobby/TokenKey is not a secrets@1 name (no slash). This is that key.
    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), "cloud", StringComparison.OrdinalIgnoreCase);

    public static IdentityPosture Posture => IdentityPosture.Secured;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [Capability.Identity.V1],
        Needs: [Need.Http, Need.Secrets],
        Settings: [Setting.Of<LobbyModuleOptions>(OptionsSection)],
        Secrets: [Secret.Named(TokenSecret)]);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<LobbyModuleOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<LobbyModuleOptions>>().Value);
        services.TryAddSingleton<LobbyVisitorBudget>();
        services.TryAddSingleton<ILobbyTokenKey, LobbyTokenKeys>();
        services.AddModuleHttpSurface<LobbyHttpSurface>();
    }
}

public sealed class LobbyModuleOptions
{
    public int VisitorsPerPrefix { get; set; } = 20;
    public string BrainId { get; set; } = "lobby";
}

// Auth stays behind this seam so the lobby project does not reference the Auth implementation.
public interface ILobbyAuth
{
    Task<LobbyAuthResult> Submit(LobbyAuthCommand command, CancellationToken cancellationToken = default);
}

public sealed record LobbyAuthCommand(string Screen, string VisitorId, string? Email, string? Secret, string? Purpose, bool Consent = false);

// A member is present only when Auth has authenticated that person. The lobby keeps it in memory until the handoff
// redeems it; persisted lobby state never stores the member.
public sealed record LobbyAuthResult(string Screen, string? Error, Member? Member);

public interface ILobbyTokenKey
{
    byte[] Key { get; }
}

internal sealed class LobbyTokenKeys : ILobbyTokenKey
{
    public byte[] Key { get; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}

[Signal("lobby.ended")]
public sealed record LobbyEnded(string HandoffCode) : Signal;
