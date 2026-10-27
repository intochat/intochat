namespace IntoChat.OS.Framework.Modules.Platform.Identity;

// What a module does when a member signs in, running as that member. Platform calls every handler
// after the session cookie is issued; a handler that fails is logged and never fails the sign-in.
public interface ISignInHandler
{
    Task SignedIn(Member member, CancellationToken cancellationToken);
}
