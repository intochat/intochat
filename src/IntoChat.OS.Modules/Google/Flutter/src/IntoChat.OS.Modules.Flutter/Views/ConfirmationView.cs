using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Capabilities;
using DigitalBrain.UI.Confirmation;
using DigitalBrain.Confirmation;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Views;

// A host-owned leaf: its buttons go straight to the runtime inbox on the viewer's HTTP session.
internal static class ConfirmationView
{
    public static DigitalBrain.UI.View Render(ConfirmationInbox inbox)
    {
        var human = RuntimeCallerContext.TryGet(out var caller) && RuntimeCallerContext.IsTrusted(caller)
            && caller.Kind == CallerKind.User && caller.StampedBy == TrustedEdge.AuthenticatedHttp
            && caller.AppId is null && caller.OnBehalfOf is null && !AppPrincipal.IsApp(caller.PrincipalId)
            && !RuntimeCallerContext.IsModelOriginated(caller);
        DigitalBrain.UI.Node[] cards = [.. inbox.Pending.Select(request =>
            (DigitalBrain.UI.Node)ConfirmView.For(request.PlanId, request.Plan.Title, request.Plan.Body, request.Plan.Expiry, human))];
        return new("Confirmations", new DigitalBrain.UI.Column(cards));
    }
}
