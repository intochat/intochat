using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Localization;
using OtpNet;
using QRCoder;

namespace IntoChat.Cloud.Auth.Ui;

public sealed class DeviceRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Current { get; set; }
}

public sealed class AccountState
{
    public string Screen { get; set; } = "profile";
    public string Name { get; set; } = "";
    public string BaselineName { get; set; } = "";
    public int Version { get; set; } = 1;
    public string? Error { get; set; }
    public bool Busy { get; set; }
    public bool GoogleOnly { get; set; }
    public bool Lobby { get; set; }
    public string Email { get; set; } = "";
    public string? PendingEmail { get; set; }
    public DateTimeOffset? EmailSent { get; set; }
    public bool EmailChanged { get; set; }
    public bool OthersSignedOut { get; set; }
    public bool Ended { get; set; }
    public string? Licence { get; set; }
    public bool StaleSignIn { get; set; }
    public bool ShowKey { get; set; }
    public bool TotpEnabled { get; set; }
    public string? Secret { get; set; }
    public string? Qr { get; set; }
    public string[] Recovery { get; set; } = [];
    public bool Ack { get; set; }
    public bool ConfirmRemove { get; set; }
    public bool ConfirmOthers { get; set; }
    public bool ConfirmRegenerate { get; set; }
    public string? AvatarError { get; set; }
    public long TotpLastStep { get; set; }
    public DeviceRow[] Devices { get; set; } = [];
}

// The Settings Account tab mounts this neuron at account/view/surface (09-04). A lobby visitor is refused.
[Neuron("view")]
internal sealed class AccountSurfaceNeuron(IAccountAuth auth, ILocalizer localizer, TimeProvider clock) : Neuron<AccountState>
{
    public const string Type = "view";
    public const string Key = "surface";
    public static readonly TimeSpan EmailCooldown = TimeSpan.FromSeconds(42);
    public const long AvatarLimit = 5 * 1024 * 1024;

    private string? pendingSecret;
    private string? pendingQr;
    private string[]? pendingCodes;
    private readonly TotpVerifier totp = new(clock);

    public string Screen => State.Screen;
    public string? Error => State.Error;
    public bool Ended => State.Ended;
    public bool OthersSignedOut => State.OthersSignedOut;
    public bool EmailChanged => State.EmailChanged;
    public int Version => State.Version;
    public bool TotpEnabled => State.TotpEnabled;
    public long TotpLastStep => State.TotpLastStep;

    public View Show(string language = "en")
    {
        var shown = Copy();
        shown.Secret = pendingSecret;
        shown.Qr = pendingQr;
        if (pendingCodes is not null) { shown.Recovery = pendingCodes; }
        return AccountScreens.Render(localizer, language, shown);
    }

    public async Task Focus(string screen)
    {
        var next = Copy();
        next.Screen = screen;
        await Save(next, new AccountChanged(screen));
    }

    public async Task Open(string name, string email, bool googleOnly, bool lobby, string? licence, DeviceRow[] devices)
    {
        var next = Copy();
        next.Lobby = lobby;
        next.Screen = lobby ? "refused" : "profile";
        next.Name = name;
        next.BaselineName = name;
        next.Email = email;
        next.GoogleOnly = googleOnly;
        next.Licence = licence;
        next.Devices = devices;
        await Save(next, new AccountChanged(next.Screen));
    }

    public async Task SaveProfile(string name, int version)
    {
        var next = Copy();
        if (version != State.Version) { next.Error = "stale"; await Save(next, new AccountChanged("profile")); return; }
        if (name == State.BaselineName) { next.Error = null; await Save(next, new AccountChanged("profile")); return; }
        next.Name = name;
        next.BaselineName = name;
        next.Version = State.Version + 1;
        next.Error = "saved";
        await Save(next, new AccountChanged("profile"));
    }

    public async Task SetAvatar(string contentType, long bytes)
    {
        var next = Copy();
        var allowed = contentType is "image/jpeg" or "image/png" && bytes > 0 && bytes <= AvatarLimit;
        next.AvatarError = allowed ? null : "type";
        await Save(next, new AccountChanged("profile"));
    }

    public async Task RequestEmail(string email)
    {
        var now = clock.GetUtcNow();
        var next = Copy();
        next.Screen = "email";
        if (State.EmailSent is { } sent && now - sent < EmailCooldown)
        {
            next.Error = "wait";
            await Save(next, new AccountChanged("email"));
            return;
        }

        next.PendingEmail = email;
        next.EmailSent = now;
        next.Error = null;
        next.Screen = "email-code";
        await auth.RequestEmailChange(email);
        await Save(next, new AccountChanged("email-code"));
    }

    public async Task ConfirmEmail(string code)
    {
        var next = Copy();
        next.Screen = "email";
        if (!await auth.ConfirmEmail(State.PendingEmail ?? State.Email, code)) { next.Error = "wrong"; await Save(next, new AccountChanged("email")); return; }
        next.Email = State.PendingEmail ?? State.Email;
        next.PendingEmail = null;
        next.EmailChanged = true;
        next.OthersSignedOut = true;
        next.Error = "changed";
        await Save(next, new AccountChanged("email"));
    }

    public async Task SavePassword(string current, string nextPassword, string confirm)
    {
        var next = Copy();
        next.Screen = "password";
        if (State.GoogleOnly) { next.Error = "google"; await Save(next, new AccountChanged("password")); return; }
        if (nextPassword != confirm || nextPassword.Length < 12 || !await auth.ChangePassword(current, nextPassword))
        {
            next.Error = "mismatch";
            await Save(next, new AccountChanged("password"));
            return;
        }
        next.OthersSignedOut = true;
        next.Error = "changed";
        await Save(next, new AccountChanged("password"));
    }

    public async Task Revoke(string id)
    {
        var next = Copy();
        next.Devices = State.Devices.Where(row => row.Current || row.Id != id).Select(Clone).ToArray();
        await Save(next, new AccountChanged("devices"));
    }

    public async Task AskRevokeOthers()
    {
        var next = Copy();
        next.Screen = "devices";
        next.ConfirmOthers = true;
        await Save(next, new AccountChanged("devices"));
    }

    public async Task RevokeOthers()
    {
        var next = Copy();
        next.ConfirmOthers = false;
        next.Devices = State.Devices.Where(row => row.Current).Select(Clone).ToArray();
        next.OthersSignedOut = true;
        next.Error = "others";
        await Save(next, new AccountChanged("devices"));
    }

    public async Task EndThisDevice()
    {
        var next = Copy();
        next.Ended = true;
        next.Screen = "login";
        await Save(next, new AccountChanged("login"));
    }

    public async Task BeginEnrol(bool recent, string email)
    {
        var next = Copy();
        next.Screen = "authenticator";
        if (!recent)
        {
            next.StaleSignIn = true;
            pendingSecret = null;
            pendingQr = null;
            await Save(next, new AccountChanged("authenticator"));
            return;
        }

        var secret = KeyGeneration.GenerateRandomKey(20);
        pendingSecret = Base32Encoding.ToString(secret);
        pendingQr = TotpQr.DataUri("otpauth://totp/IntoChat:" + email + "?secret=" + pendingSecret + "&issuer=IntoChat");
        next.StaleSignIn = false;
        next.ShowKey = false;
        await Save(next, new AccountChanged("authenticator"));
    }

    public async Task ToggleKey()
    {
        var next = Copy();
        next.ShowKey = !State.ShowKey;
        await Save(next, new AccountChanged("authenticator"));
    }

    public async Task SubmitTotp(string code)
    {
        var next = Copy();
        next.Screen = "authenticator";
        if (pendingSecret is null) { next.Error = "wrong"; await Save(next, new AccountChanged("authenticator")); return; }
        var result = totp.Verify(Base32Encoding.ToBytes(pendingSecret), code, State.TotpLastStep);
        if (!result.Ok)
        {
            next.Error = result.Replay ? "reused" : "wrong";
            await Save(next, new AccountChanged("authenticator"));
            return;
        }

        next.TotpEnabled = true;
        next.TotpLastStep = result.MatchedStep;
        next.Error = null;
        next.Screen = "recovery";
        next.Ack = false;
        pendingCodes = RecoveryCodes();
        await Save(next, new AccountChanged("recovery"));
    }

    public async Task Acknowledge()
    {
        var next = Copy();
        next.Ack = true;
        await Save(next, new AccountChanged("recovery"));
    }

    public async Task FinishRecovery()
    {
        if (!State.Ack) { return; }
        pendingCodes = null;
        var next = Copy();
        next.Screen = "profile";
        next.Recovery = [];
        await Save(next, new AccountChanged("profile"));
    }

    public async Task AskRemove()
    {
        var next = Copy();
        next.ConfirmRemove = true;
        next.Screen = "authenticator";
        await Save(next, new AccountChanged("authenticator"));
    }

    public async Task RemoveTotp()
    {
        pendingSecret = null;
        pendingQr = null;
        var next = Copy();
        next.ConfirmRemove = false;
        next.TotpEnabled = false;
        next.OthersSignedOut = true;
        next.Error = "removed";
        next.Screen = "authenticator";
        await Save(next, new AccountChanged("authenticator"));
    }

    public async Task AskRegenerate()
    {
        var next = Copy();
        next.ConfirmRegenerate = true;
        next.Screen = "recovery";
        await Save(next, new AccountChanged("recovery"));
    }

    public async Task Regenerate()
    {
        var next = Copy();
        next.ConfirmRegenerate = false;
        next.Ack = false;
        next.Screen = "recovery";
        pendingCodes = RecoveryCodes();
        await Save(next, new AccountChanged("recovery"));
    }

    private AccountState Copy() => new()
    {
        Screen = State.Screen,
        Name = State.Name,
        BaselineName = State.BaselineName,
        Version = State.Version,
        Error = State.Error,
        Busy = State.Busy,
        GoogleOnly = State.GoogleOnly,
        Lobby = State.Lobby,
        Email = State.Email,
        PendingEmail = State.PendingEmail,
        EmailSent = State.EmailSent,
        EmailChanged = State.EmailChanged,
        OthersSignedOut = State.OthersSignedOut,
        Ended = State.Ended,
        Licence = State.Licence,
        StaleSignIn = State.StaleSignIn,
        ShowKey = State.ShowKey,
        TotpEnabled = State.TotpEnabled,
        Secret = null,
        Qr = null,
        Recovery = [],
        Ack = State.Ack,
        ConfirmRemove = State.ConfirmRemove,
        ConfirmOthers = State.ConfirmOthers,
        ConfirmRegenerate = State.ConfirmRegenerate,
        AvatarError = State.AvatarError,
        TotpLastStep = State.TotpLastStep,
        Devices = State.Devices.Select(Clone).ToArray(),
    };

    private static DeviceRow Clone(DeviceRow row) => new() { Id = row.Id, Label = row.Label, Current = row.Current };

    private static string[] RecoveryCodes() => Enumerable.Range(0, 10)
        .Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant())
        .ToArray();
}

[Signal("account.changed")]
public sealed record AccountChanged(string Screen) : Signal;

// The account surface reaches codes and passwords through this seam. 09-09 binds it to AuthCode and AuthCredential.
public interface IAccountAuth
{
    Task<bool> ConfirmEmail(string email, string code, CancellationToken cancellationToken = default);

    Task<bool> ChangePassword(string current, string next, CancellationToken cancellationToken = default);

    // Issues the change-email code. The default does nothing so a test double that only answers the two checks still compiles.
    Task RequestEmailChange(string email, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class UnconfiguredAccountAuth : IAccountAuth
{
    public Task<bool> ConfirmEmail(string email, string code, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> ChangePassword(string current, string next, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public static class TotpQr
{
    public static string DataUri(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(4);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}

public static class AccountScreens
{
    public const string QrAlt = "QR code for your authenticator app";

    public static readonly string[] GoldenNames =
    [
        "profile.default", "profile.error", "profile.loading",
        "email.default", "email.error", "email.loading",
        "password.default", "password.error", "password.loading",
        "authenticator.default", "authenticator.error", "authenticator.loading",
        "recovery.default", "recovery.error", "recovery.loading",
        "devices.default", "devices.error", "devices.loading",
        "licence.default", "licence.error", "licence.loading",
    ];

    public static View Golden(ILocalizer text, string name)
    {
        var state = name switch
        {
            "profile.error" => Sample(),
            "profile.loading" => Sample(busy: true),
            "email.default" => Sample(screen: "email"),
            "email.error" => Sample(screen: "email", error: "wrong"),
            "email.loading" => Sample(screen: "email", busy: true),
            "password.default" => Sample(screen: "password"),
            "password.error" => Sample(screen: "password", error: "mismatch"),
            "password.loading" => Sample(screen: "password", busy: true),
            "authenticator.default" => Enrol(),
            "authenticator.error" => Enrol(error: "wrong"),
            "authenticator.loading" => Enrol(busy: true),
            "recovery.default" => Sample(screen: "recovery", recovery: ["a1b2c3d4", "e5f60718"]),
            "recovery.error" => Sample(screen: "recovery", error: "removed", recovery: ["a1b2c3d4"]),
            "recovery.loading" => Sample(screen: "recovery", busy: true, recovery: ["a1b2c3d4"]),
            "devices.default" => Sample(screen: "devices"),
            "devices.error" => Sample(screen: "devices", error: "others"),
            "devices.loading" => Sample(screen: "devices", busy: true),
            "licence.default" => Sample(screen: "licence", licence: "Renews 2027-01-01"),
            "licence.error" => Sample(screen: "licence", licence: "Expired", error: "stale"),
            "licence.loading" => Sample(screen: "licence", licence: "Renews 2027-01-01", busy: true),
            _ => Sample(),
        };
        if (name == "profile.error") { state.Error = "stale"; state.AvatarError = "type"; }
        return Render(text, "en", state);
    }

    public static View Render(ILocalizer text, string language, AccountState state)
    {
        string T(string key) => text.Get(language, key);
        if (state.Lobby) { return new View("refused", new Column([new Text(T("Account.Refused"))])); }
        var nodes = new List<Node>
        {
            new Text(T("Account.Profile.Title")),
            new Text(T("Account.Profile.Subtitle")),
        };
        if (state.Screen is "profile" or "email" or "password" or "authenticator" or "recovery" or "devices" or "licence")
        {
            nodes.Add(Section(text, language, state));
        }
        return new View(state.Screen, new Column(nodes));
    }

    private static Node Section(ILocalizer text, string language, AccountState state)
    {
        string T(string key) => text.Get(language, key);
        var nodes = new List<Node>();
        switch (state.Screen)
        {
            case "email":
            case "email-code":
                nodes.Add(new Text(T("Account.Email.Title")));
                nodes.Add(new TextField("email", state.PendingEmail ?? state.Email, T("Account.Profile.Email")));
                nodes.Add(Button("send", state.Busy ? T("Account.Email.Send") : T("Account.Email.Send"), !state.Busy));
                if (state.Screen == "email-code") { nodes.Add(new TextField("code", null, T("Account.Email.Code"))); nodes.Add(Button("confirm", T("Account.Email.Confirm"), !state.Busy)); }
                break;
            case "password":
                nodes.Add(new Text(T("Account.Password.Title")));
                if (state.GoogleOnly) { nodes.Add(new Text(T("Account.Password.Google"))); }
                else
                {
                    nodes.Add(new TextField("current", null, T("Account.Password.Current")));
                    nodes.Add(new TextField("password", null, T("Account.Password.New")));
                    nodes.Add(Button("save", T("Account.Password.Save"), !state.Busy));
                }
                break;
            case "authenticator":
                nodes.Add(new Text(T("Account.Totp.Title")));
                if (state.StaleSignIn) { nodes.Add(new Text(T("Account.Totp.Stale"))); nodes.Add(Button("signin", T("Account.Totp.SignIn"), true, ButtonTone.Neutral)); }
                else
                {
                    if (state.Qr is not null) { nodes.Add(new Text(QrAlt)); nodes.Add(new Text(state.Qr)); }
                    nodes.Add(Button("key", state.ShowKey ? T("Account.Totp.Hide") : T("Account.Totp.Show"), true, ButtonTone.Neutral));
                    if (state.ShowKey && state.Secret is not null) { nodes.Add(new Text(state.Secret)); }
                    nodes.Add(new TextField("code", null, T("Account.Email.Code")));
                    nodes.Add(Button("verify", T("Account.Email.Confirm"), !state.Busy));
                    if (state.ConfirmRemove)
                    {
                        nodes.Add(new Card(T("Account.Totp.Remove"), T("Account.Totp.RemoveBody"), [Button("confirm-remove", T("Account.Totp.Confirm"), true), Button("cancel", T("Account.Totp.Cancel"), true, ButtonTone.Neutral)]));
                    }
                    else { nodes.Add(Button("remove", T("Account.Totp.Remove"), true, ButtonTone.Neutral)); }
                }
                break;
            case "recovery":
                nodes.Add(new Text(T("Account.Recovery.Heading")));
                nodes.Add(new Text(T("Account.Recovery.Body")));
                foreach (var code in state.Recovery) { nodes.Add(new Text(code)); }
                nodes.Add(Button("download", T("Account.Recovery.Download"), state.Recovery.Length > 0, ButtonTone.Neutral));
                nodes.Add(Button("ack", T("Account.Recovery.Ack"), true, ButtonTone.Neutral));
                nodes.Add(Button("done", T("Account.Recovery.Done"), state.Ack && !state.Busy));
                if (state.ConfirmRegenerate)
                {
                    nodes.Add(new Card(T("Account.Recovery.Regenerate"), T("Account.Recovery.RegenerateBody"), [Button("confirm-regenerate", T("Account.Totp.Confirm"), true), Button("cancel", T("Account.Totp.Cancel"), true, ButtonTone.Neutral)]));
                }
                break;
            case "devices":
                nodes.Add(new Text(T("Account.Devices.Title")));
                nodes.Add(new Text(T("Account.Devices.Body")));
                foreach (var device in state.Devices.OrderByDescending(row => row.Current))
                {
                    nodes.Add(new Text(device.Current ? T("Account.Devices.This") : device.Label));
                    if (!device.Current) { nodes.Add(Button("logout:" + device.Id, T("Account.Devices.LogOut"), !state.Busy, ButtonTone.Neutral)); }
                }
                if (state.ConfirmOthers)
                {
                    nodes.Add(new Card(T("Account.Devices.LogOutOthers"), T("Account.Devices.ConfirmBody"), [Button("confirm-others", T("Account.Totp.Confirm"), true), Button("cancel", T("Account.Totp.Cancel"), true, ButtonTone.Neutral)]));
                }
                else { nodes.Add(Button("others", T("Account.Devices.LogOutOthers"), !state.Busy, ButtonTone.Neutral)); }
                nodes.Add(Button("this", T("Account.Devices.LogOutThis"), !state.Busy, ButtonTone.Neutral));
                break;
            case "licence":
                nodes.Add(new Text(T("Account.Licence.Title")));
                if (state.Licence is not null) { nodes.Add(new Text(state.Licence)); }
                break;
            default:
                nodes.Add(new TextField("name", state.Name, T("Account.Profile.Name")));
                nodes.Add(Button("save", state.Busy ? T("Account.Profile.Save") : T("Account.Profile.Save"), !state.Busy && state.Name != state.BaselineName));
                break;
        }

        if (state.Error is "stale") { nodes.Add(new Text(T("Account.Profile.Stale"))); }
        if (state.AvatarError is "type") { nodes.Add(new Text(T("Account.Profile.Avatar"))); }
        if (state.Error is "saved") { nodes.Add(new Text(T("Account.Profile.Saved"))); }
        if (state.Error is "wait") { nodes.Add(new Text(T("Account.Email.Wait"))); }
        if (state.Error is "wrong") { nodes.Add(new Text(T("Account.Totp.Wrong"))); }
        if (state.Error is "reused") { nodes.Add(new Text(T("Account.Totp.Reused"))); }
        if (state.Error is "changed" && state.Screen == "email") { nodes.Add(new Text(T("Account.Email.Changed"))); }
        if (state.Error is "changed" && state.Screen == "password") { nodes.Add(new Text(T("Account.Password.Changed"))); }
        if (state.Error is "mismatch") { nodes.Add(new Text(T("Account.Password.Mismatch"))); }
        if (state.Error is "removed") { nodes.Add(new Text(T("Account.Totp.Removed"))); }
        if (state.Error is "others") { nodes.Add(new Text(T("Account.Devices.OthersSignedOut"))); }
        return new Column(nodes);
    }

    private static Button Button(string operation, string label, bool enabled, string tone = ButtonTone.Accent) =>
        new(label, new ButtonAction("account", operation, []), enabled, tone);

    private static AccountState Sample(string screen = "profile", string? error = null, bool busy = false, string? licence = null, string[]? recovery = null) => new()
    {
        Screen = screen,
        Name = "Ada",
        BaselineName = "Ada",
        Email = "ada@example.com",
        Error = error,
        Busy = busy,
        Licence = licence,
        Recovery = recovery ?? [],
        Devices = [new DeviceRow { Id = "this", Label = "This device", Current = true }, new DeviceRow { Id = "other", Label = "Chrome", Current = false }],
    };

    private static AccountState Enrol(string? error = null, bool busy = false)
    {
        var state = Sample(screen: "authenticator", error: error, busy: busy);
        state.Secret = "JBSWY3DPEHPK3PXP";
        state.Qr = TotpQr.DataUri("otpauth://totp/IntoChat:ada@example.com?secret=JBSWY3DPEHPK3PXP&issuer=IntoChat");
        return state;
    }
}
