namespace IntoChat.OS.Framework.Platform.Identity;

public enum MemberRole
{
    Owner = 0,
    Member = 1,
    // An installed app's own principal (AppPrincipal); it acts only behind the app proxy.
    App = 2,
}
