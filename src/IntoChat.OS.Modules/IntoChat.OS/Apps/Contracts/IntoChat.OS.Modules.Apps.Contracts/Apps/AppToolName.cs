namespace IntoChat.OS.Modules.Apps;

// The assistant-tool name of an app operation: stable, lowercase, underscore-separated.
public static class AppToolName
{
    public static string For(string app, string operation)
        => "app_" + Sanitize(app) + "_" + Sanitize(operation);

    private static string Sanitize(string value)
        => new([.. value.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '_')]);
}
