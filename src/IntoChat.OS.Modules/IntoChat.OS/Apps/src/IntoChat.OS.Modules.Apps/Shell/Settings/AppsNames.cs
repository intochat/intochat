using IntoChat.OS.Modules.Apps;

// What Apps opens on: the library, one listed app (a release pinned or the latest), or an installed app by name.
public sealed record AppsTarget(string? Publisher = null, string? App = null, string? Version = null, string? Installed = null)
{
    public bool IsLibrary => App is null && Installed is null;

    public override string ToString() => Installed is { } name ? "installed:" + name
        : App is null ? "" : "app:" + Publisher + "/" + App + (Version is null ? "" : "@" + Version);
}

// How Apps names an app to a person, and the link that opens one on this host. The publisher the OS ships under
// ("os") shows as "intochat"; the internal publisher is unchanged everywhere else.
public static class AppsNames
{
    public const string ShippedDisplay = "intochat";

    public static string Display(string publisher, string app)
        => (publisher == ShippedApps.Publisher ? ShippedDisplay : publisher) + "/" + app;

    // An absolute link on the host's public origin: https, or http on loopback only, rebuilt from scheme, host and
    // port (no user info, path or query). Null when the host has none, so the detail says sharing is off.
    public static string? ShareLink(string? publicOrigin, string publisher, string app, string? version)
    {
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
            || !(origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            || publisher.Length == 0 || app.Length == 0)
        { return null; }
        return origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            + "/#/open?application=" + Uri.EscapeDataString(app) + "&publisher=" + Uri.EscapeDataString(publisher)
            + (version is { Length: > 0 } ? "&release=" + Uri.EscapeDataString(version) : "");
    }

    // "" is the library; "app:<publisher>/<app>[@<version>]" a listed app; "installed:<name>" an installed one.
    // Anything else, or a malformed name or version, is the library: a bad link navigates nowhere.
    public static AppsTarget Parse(string? input)
    {
        if (input is not { Length: > 0 and <= 200 } text) { return new(); }
        if (text.StartsWith("installed:", StringComparison.Ordinal))
        { return IsName(text["installed:".Length..]) ? new(Installed: text["installed:".Length..]) : new(); }
        if (!text.StartsWith("app:", StringComparison.Ordinal)) { return new(); }
        var rest = text["app:".Length..];
        string? version = null;
        if (rest.IndexOf('@', StringComparison.Ordinal) is var at and >= 0)
        {
            version = rest[(at + 1)..];
            rest = rest[..at];
            if (!IsVersion(version)) { return new(); }
        }
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) { return new(); }
        var (publisher, app) = (rest[..slash], rest[(slash + 1)..]);
        return IsName(publisher) && IsName(app) ? new(publisher, app, version) : new();
    }

    public static bool IsName(string value)
        => value.Length is > 0 and <= 64 && char.IsAsciiLetterLower(value[0])
            && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    // Three dot-separated numbers, as releases are versioned.
    public static bool IsVersion(string value)
    {
        var parts = value.Split('.');
        return value.Length <= 32 && parts.Length == 3 && parts.All(part => part.Length is > 0 and <= 9 && part.All(char.IsAsciiDigit));
    }
}
