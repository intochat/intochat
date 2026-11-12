using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.Cloud.Localization;

namespace IntoChat.Cloud.Lobby;

public sealed class LobbyState
{
    public string Screen { get; set; } = "login";
    public string? Email { get; set; }
    public string? Purpose { get; set; }
    public string? Error { get; set; }
    public bool Busy { get; set; }
    public bool Consent { get; set; }
    public bool FieldsReady { get; set; }
    public string? HandoffCode { get; set; }
    public DateTimeOffset? HandoffExpires { get; set; }
    public string? GoogleNonce { get; set; }
}

[Neuron("lobby.visitor")]
internal sealed class LobbyNeuron(ILobbyAuth auth, ILocalizer localizer, TimeProvider clock) : Neuron<LobbyState>
{
    public const string Type = "lobby.visitor";
    public static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(60);

    // The authenticated member stays on this instance until POST /lobby/handoff redeems the code. It is not persisted,
    // so a restarted neuron cannot mint a session from a leftover code.
    private Member? pending;

    public string Screen => State.Screen;
    public string? Error => State.Error;
    public string? Email => State.Email;
    public string? Purpose => State.Purpose;
    public string? HandoffCode => State.HandoffCode;
    public string? GoogleNonce => State.GoogleNonce;
    public bool HoldsAccountData => State.Email is not null || State.Purpose is not null;

    public View Show(string language = "en") => LobbyScreens.Render(localizer, language, State);

    public async Task<LobbyAuthResult> Submit(LobbyAuthCommand command)
    {
        var result = await auth.Submit(command);
        var next = Copy();
        next.Screen = result.Screen;
        next.Error = result.Error;
        next.Email = command.Email ?? next.Email;
        next.Purpose = command.Purpose ?? next.Purpose;
        next.Consent = command.Consent;
        next.Busy = false;
        if (result.Member is { } member)
        {
            pending = member;
            next.HandoffCode = Base64Url(RandomNumberGenerator.GetBytes(32));
            next.HandoffExpires = clock.GetUtcNow() + HandoffLifetime;
            next.Email = null;
            next.Purpose = null;
            await Save(next, new LobbyEnded(next.HandoffCode));
            return result;
        }

        pending = null;
        next.HandoffCode = null;
        next.HandoffExpires = null;
        await Save(next, new LobbyScreenChanged(next.Screen));
        return result;
    }

    public async Task FinishReset()
    {
        pending = null;
        var next = Copy();
        next.Screen = "reset-done";
        next.Error = null;
        next.Email = null;
        next.Purpose = null;
        next.HandoffCode = null;
        next.HandoffExpires = null;
        await Save(next, new LobbyScreenChanged("reset-done"));
    }

    public async Task ReturnToLogin()
    {
        pending = null;
        var next = Copy();
        next.Screen = "login";
        next.Error = null;
        next.HandoffCode = null;
        next.HandoffExpires = null;
        await Save(next, new LobbyScreenChanged("login"));
    }

    public async Task<string> BeginGoogle()
    {
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var next = Copy();
        next.Screen = "google";
        next.GoogleNonce = nonce;
        next.Error = null;
        await Save(next, new LobbyScreenChanged("google"));
        return nonce;
    }

    public async Task RejectGoogle()
    {
        pending = null;
        var next = Copy();
        next.Screen = "google";
        next.Error = "failed";
        next.HandoffCode = null;
        next.HandoffExpires = null;
        await Save(next, new LobbyScreenChanged("google"));
    }

    public async Task<Member?> Redeem(string code, string visitorId, DateTimeOffset now)
    {
        if (pending is null || State.HandoffCode is null || State.HandoffCode != code || State.HandoffExpires is null || State.HandoffExpires <= now)
        {
            return null;
        }

        if (Id.Key != visitorId) { return null; }
        var member = pending;
        pending = null;
        var next = Copy();
        next.HandoffCode = null;
        next.HandoffExpires = null;
        next.Email = null;
        next.Purpose = null;
        await Save(next, new LobbyScreenChanged("handed-off"));
        return member;
    }

    private LobbyState Copy() => new()
    {
        Screen = State.Screen,
        Email = State.Email,
        Purpose = State.Purpose,
        Error = State.Error,
        Busy = State.Busy,
        Consent = State.Consent,
        FieldsReady = State.FieldsReady,
        HandoffCode = State.HandoffCode,
        HandoffExpires = State.HandoffExpires,
        GoogleNonce = State.GoogleNonce,
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

[Signal("lobby.screen-changed")]
public sealed record LobbyScreenChanged(string Screen) : Signal;
