using DigitalBrain.UI;
using IntoChat.Cloud.Localization;

namespace IntoChat.Cloud.Lobby;

public static class LobbyScreens
{
    // The 29 trees named in 09-07. reset-request.error is the enumeration-safe "code sent" state.
    public static readonly string[] GoldenNames =
    [
        "login.default", "login.error", "login.loading",
        "signup.default", "signup.error", "signup.loading",
        "code.default", "code.error", "code.loading",
        "factor.default", "factor.error", "factor.loading",
        "reset-request.default", "reset-request.error", "reset-request.loading",
        "reset-code.default", "reset-code.error", "reset-code.loading",
        "reset-new.default", "reset-new.error", "reset-new.loading",
        "reset-done.default",
        "google.default", "google.error", "google.loading",
        "lobby.timeout", "lobby.unavailable", "lobby.upgrading", "lobby.offline",
    ];

    public static View Golden(ILocalizer text, string name)
    {
        var state = name switch
        {
            "login.error" => new LobbyState { Screen = "login", Error = "wrong" },
            "login.loading" => new LobbyState { Screen = "login", Busy = true },
            "signup.default" => new LobbyState { Screen = "signup" },
            "signup.error" => new LobbyState { Screen = "signup", Error = "invalid" },
            "signup.loading" => new LobbyState { Screen = "signup", Busy = true },
            "code.default" => new LobbyState { Screen = "code", Email = "a***@example.com" },
            "code.error" => new LobbyState { Screen = "code", Error = "wrong" },
            "code.loading" => new LobbyState { Screen = "code", Busy = true },
            "factor.default" => new LobbyState { Screen = "factor" },
            "factor.error" => new LobbyState { Screen = "factor", Error = "wrong" },
            "factor.loading" => new LobbyState { Screen = "factor", Busy = true },
            "reset-request.default" => new LobbyState { Screen = "reset-request" },
            "reset-request.error" => new LobbyState { Screen = "reset-request", Error = "sent" },
            "reset-request.loading" => new LobbyState { Screen = "reset-request", Busy = true },
            "reset-code.default" => new LobbyState { Screen = "reset-code", Email = "a***@example.com" },
            "reset-code.error" => new LobbyState { Screen = "reset-code", Error = "wrong" },
            "reset-code.loading" => new LobbyState { Screen = "reset-code", Busy = true },
            "reset-new.default" => new LobbyState { Screen = "reset-new" },
            "reset-new.error" => new LobbyState { Screen = "reset-new", Error = "mismatch" },
            "reset-new.loading" => new LobbyState { Screen = "reset-new", Busy = true },
            "reset-done.default" => new LobbyState { Screen = "reset-done" },
            "google.default" => new LobbyState { Screen = "google" },
            "google.error" => new LobbyState { Screen = "google", Error = "failed" },
            "google.loading" => new LobbyState { Screen = "google", Busy = true },
            "lobby.timeout" => new LobbyState { Screen = "timeout" },
            "lobby.unavailable" => new LobbyState { Screen = "unavailable" },
            "lobby.upgrading" => new LobbyState { Screen = "upgrading" },
            "lobby.offline" => new LobbyState { Screen = "offline" },
            _ => new LobbyState { Screen = "login" },
        };
        return Render(text, "en", state);
    }

    public static View Render(ILocalizer text, string language, LobbyState state)
    {
        string T(string key) => text.Get(language, key);
        var nodes = new List<Node>();
        switch (state.Screen)
        {
            case "signup":
                nodes.Add(new Text(T("Lobby.Signup.Heading")));
                nodes.Add(new Text(T("Lobby.Signup.Subtitle")));
                nodes.Add(TextInput("first", T("Lobby.Signup.FirstName")));
                nodes.Add(TextInput("last", T("Lobby.Signup.LastName")));
                nodes.Add(TextInput("email", T("Lobby.Signup.Email")));
                nodes.Add(TextInput("password", T("Lobby.Signup.Password")));
                nodes.Add(new Text(T("Lobby.Signup.PasswordRules")));
                nodes.Add(new Text(T("Lobby.Signup.Consent")));
                nodes.Add(ButtonOf("consent", state.Consent ? T("Lobby.Signup.Consent") : T("Lobby.Signup.ConsentRequired"), true, ButtonTone.Neutral));
                nodes.Add(ButtonOf("submit", state.Busy ? T("Lobby.Login.Submitting") : T("Lobby.Signup.Submit"), !state.Busy && state.FieldsReady && state.Consent));
                break;
            case "code":
            case "reset-code":
                nodes.Add(new Text(T("Lobby.Code.Heading")));
                nodes.Add(new Text(T("Lobby.Code.Subtitle")));
                if (state.Email is not null) { nodes.Add(new Text(state.Email)); }
                nodes.Add(TextInput("code", T("Lobby.Code.Field")));
                nodes.Add(ButtonOf("verify", state.Busy ? T("Lobby.Code.Verifying") : T("Lobby.Code.Verify"), !state.Busy));
                nodes.Add(ButtonOf("resend", T("Lobby.Code.Resend"), !state.Busy, ButtonTone.Neutral));
                break;
            case "factor":
                nodes.Add(new Text(T("Lobby.Factor.Heading")));
                nodes.Add(new Text(T("Lobby.Factor.Subtitle")));
                nodes.Add(TextInput("code", T("Lobby.Code.Field")));
                nodes.Add(ButtonOf("verify", state.Busy ? T("Lobby.Code.Verifying") : T("Lobby.Code.Verify"), !state.Busy));
                nodes.Add(ButtonOf("email", T("Lobby.Login.Forgot"), true, ButtonTone.Neutral));
                break;
            case "reset-request":
                nodes.Add(new Text(T("Lobby.Reset.Heading")));
                nodes.Add(new Text(T("Lobby.Reset.Body")));
                nodes.Add(TextInput("email", T("Lobby.Signup.Email")));
                nodes.Add(ButtonOf("send", state.Busy ? T("Lobby.Login.Submitting") : T("Lobby.Login.Submit"), !state.Busy));
                break;
            case "reset-new":
                nodes.Add(new Text(T("Lobby.Reset.NewHeading")));
                nodes.Add(new Text(T("Lobby.Signup.PasswordRules")));
                nodes.Add(TextInput("password", T("Lobby.Signup.Password")));
                nodes.Add(TextInput("confirm", T("Lobby.Reset.Save")));
                nodes.Add(ButtonOf("save", T("Lobby.Reset.Save"), !state.Busy));
                break;
            case "reset-done":
                nodes.Add(new Text(T("Lobby.Reset.Done")));
                nodes.Add(ButtonOf("login", T("Lobby.Login.Submit"), true));
                break;
            case "google":
                nodes.Add(new Text(state.Busy ? T("Lobby.Login.Submitting") : T("Lobby.Google.Opening")));
                nodes.Add(new Text(T("Lobby.Google.Return")));
                nodes.Add(ButtonOf("cancel", T("Lobby.Login.Submit"), !state.Busy, ButtonTone.Neutral));
                break;
            case "timeout":
                nodes.Add(new Text(T("Lobby.Session.Timeout")));
                break;
            case "unavailable":
                nodes.Add(new Text(T("Lobby.Unavailable.Heading")));
                nodes.Add(new Text(T("Lobby.Login.Unavailable")));
                break;
            case "upgrading":
                nodes.Add(new Text(T("Lobby.Upgrade")));
                break;
            case "offline":
                nodes.Add(new Text(T("Lobby.Offline")));
                break;
            default:
                nodes.Add(new Text(T("Lobby.Login.Heading")));
                nodes.Add(new Text(T("Lobby.Login.Subtitle")));
                nodes.Add(ButtonOf("google", T("Lobby.Login.Google"), state.Error != "unavailable", ButtonTone.Neutral));
                nodes.Add(new Text(T("Lobby.Login.Or")));
                nodes.Add(TextInput("email", T("Lobby.Login.Email")));
                nodes.Add(TextInput("password", T("Lobby.Login.Password")));
                nodes.Add(ButtonOf("forgot", T("Lobby.Login.Forgot"), true, ButtonTone.Neutral));
                nodes.Add(ButtonOf("submit", state.Busy ? T("Lobby.Login.Submitting") : T("Lobby.Login.Submit"), !state.Busy && state.Error != "rate" && state.Error != "unavailable"));
                nodes.Add(ButtonOf("signup", T("Lobby.Login.Create"), true, ButtonTone.Neutral));
                break;
        }

        if (state.Error is "wrong" or "unknown") { nodes.Add(new Text(state.Screen is "code" or "factor" or "reset-code" ? T("Lobby.Code.Wrong") : T("Lobby.Login.Wrong"))); }
        if (state.Error is "rate") { nodes.Add(new Text(T("Lobby.Login.Wait"))); }
        if (state.Error is "unavailable" && state.Screen == "login") { nodes.Add(new Text(T("Lobby.Login.Unavailable"))); }
        if (state.Error is "invalid" or "exists") { nodes.Add(new Text(T("Lobby.Signup.Invalid"))); }
        if (state.Error is "expired") { nodes.Add(new Text(T("Lobby.Code.Expired"))); }
        if (state.Error is "resent") { nodes.Add(new Text(T("Lobby.Code.Resent"))); }
        if (state.Error is "sent") { nodes.Add(new Text(T("Lobby.Reset.Sent"))); }
        if (state.Error is "mismatch") { nodes.Add(new Text(T("Lobby.Signup.PasswordRules"))); }
        if (state.Error is "failed") { nodes.Add(new Text(T("Lobby.Google.Failed"))); }
        return new View(state.Screen, new Column(nodes));
    }

    private static Field TextInput(string name, string label)
        => new(name, name, name is "password" or "confirm" ? FieldKind.Password : FieldKind.Text, label);

    private static Button ButtonOf(string operation, string label, bool enabled, string tone = ButtonTone.Accent) =>
        new(operation, label, new Emit("lobby", operation), Enabled: enabled, Tone: tone);
}
