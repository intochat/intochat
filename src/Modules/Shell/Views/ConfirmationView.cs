using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.UI.Confirmation;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Views;

// A host-owned leaf: its buttons go straight to the runtime inbox on the viewer's HTTP session.
internal static class ConfirmationView
{
    public static DigitalBrain.UI.View Render(ConfirmationInbox inbox, DigitalBrain.Access.Caller? caller)
    {
        var human = caller is { Direct: true, Conversation: null, Actor: DigitalBrain.Access.Actor.Person person }
            && !AppPrincipal.IsApp(person.Principal);
        DigitalBrain.UI.Node[] cards = [.. inbox.Pending.Select(request =>
            (DigitalBrain.UI.Node)ConfirmView.For(request.PlanId, request.Plan.Title, request.Plan.Body, request.Plan.Expiry, human))];
        return new("Confirmations", new DigitalBrain.UI.Column(cards));
    }
}
