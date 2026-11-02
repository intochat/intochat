// os-only: os MemberRole values (Owner, Member, App) of the Identity directory.
using DigitalBrain.Capabilities;
namespace IntoChat.OS.Framework.Modules.Platform.Identity;

public enum MemberRole
{
    Owner = 0,
    Member = 1,
    // An installed app's own principal (AppPrincipal); it acts only behind the app proxy.
    App = 2,
}
