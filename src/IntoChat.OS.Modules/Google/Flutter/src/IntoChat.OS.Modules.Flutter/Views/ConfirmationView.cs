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
    public static UiView Render(ConfirmationInbox inbox)
    {
        var human = RuntimeCallerContext.TryGet(out var caller) && RuntimeCallerContext.IsTrusted(caller)
            && caller.Kind == CallerKind.User && caller.StampedBy == TrustedEdge.AuthenticatedHttp
            && caller.AppId is null && caller.OnBehalfOf is null && !AppPrincipal.IsApp(caller.PrincipalId)
            && !RuntimeCallerContext.IsModelOriginated(caller);
        var cards = inbox.Pending.Select((request, index) =>
        {
            var plan = request.Plan;
            var card = ConfirmView.For(request.PlanId, plan.Title, plan.Body, plan.Expiry, human);
            var key = "confirmation-" + index;
            return Ui.Card(key, card.Title, card.Body, card.Children.SelectMany((node, child) => node switch
            {
                DigitalBrain.UI.Text text => new[] { Ui.Text(key + "-" + child, text.Value) },
                DigitalBrain.UI.ApprovalCardNode { Resolve: { } resolve } =>
                [
                    Ui.Button(key + "-approve", plan.ApproveLabel, ConfirmationActions.Format(resolve.PlanId, true), true, UiStyle.Primary),
                    Ui.Button(key + "-deny", plan.DenyLabel, ConfirmationActions.Format(resolve.PlanId, false), true, UiStyle.Secondary),
                ],
                DigitalBrain.UI.ApprovalCardNode => [],
                _ => throw new InvalidOperationException("Unsupported confirmation card node."),
            }).ToArray());
        }).ToArray();
        return new(inbox.Revision, Math.Max(1, inbox.Revision), Ui.Column("confirmations", cards));
    }

    public static (string PlanId, bool Approve) Answer(string? action) => ConfirmationActions.Answer(action);
}
