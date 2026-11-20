namespace DigitalBrain.Capabilities.Identity;

public static class GrantRelations
{
    public const string View = "view";
    public const string Run = "run";
    public const string Post = "post";
    public const string Subscribe = "subscribe";
    public const string Install = "install";
    public const string Admin = "admin";
    public const string UseViaPrefix = "use-via:";

    public static bool IsUseVia(string? scope) => scope != null && scope.StartsWith(UseViaPrefix, StringComparison.Ordinal);
}
