using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Apps.Tests;

// A brain owner acting through the platform edge: publishing and installing act in the
// stamped caller's brain, so facts that cross brains stamp who they speak for.
internal static class AppOwner
{
    public static string Scope(string principal) => BrainScope.Create("account-" + principal, principal).Id;

    // The publisher's brain exists on this machine, so the Marketplace accepts its listings (R54, R55).
    public static Task Own(DigitalBrain.Testing.ITestBrain brain, string principal)
        => DigitalBrain.Testing.Module.BrainIdentityExtensions.OwnBrainAsync(brain, "account-" + principal, principal, principal);

    public static void Stamp(string principal) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = principal,
        AccountId = "account-" + principal,
        BrainId = principal,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });
}
