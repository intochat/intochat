
namespace DigitalBrain.Modules.Apps.Tests;

// A brain owner acting through the platform edge: publishing and installing act in the
// stamped caller's brain, so facts that cross brains stamp who they speak for.
internal static class Owner
{
    public static string Scope(string principal) => BrainScope.Create("account-" + principal, principal).Id;

    public static void Stamp(string principal) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = principal,
        AccountId = "account-" + principal,
        BrainId = principal,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });
}
