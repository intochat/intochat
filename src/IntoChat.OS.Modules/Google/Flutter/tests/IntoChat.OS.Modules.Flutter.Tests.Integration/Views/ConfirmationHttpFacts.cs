using System.Net;
using DigitalBrain.Confirmation;
using DigitalBrain.Runtime.Orleans.Operations;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Views;

public sealed class ConfirmationHttpFacts(FlutterHost host)
{
    [Fact]
    public async Task TheRuntimeCardIsReadAndAnsweredDirectlyOnThePersonsHttpSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        var contexts = host.Brain.Services.GetRequiredService<IConfirmationContexts>();
        var name = IConfirmationInbox.Key(person.BrainId);
        var inbox = host.Brain.Get<IConfirmationInbox>(name);
        var id = "cf-" + Guid.NewGuid().ToString("N");
        Task<ConfirmationResult> answer;
        using (person.AsApp(person.Key("apps/files")))
        {
            answer = contexts.For(new(person.BrainId), person.Key("files/main")).Confirm(
                new(id, ConfirmationAction.Delete, true, "Delete report.txt?", "This removes report.txt.", "Delete", "Keep", "report.txt"), ct);
        }
        try
        {
            using (person.AsCaller())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (!(await inbox.Read()).Pending.Any(item => item.PlanId == id)) { await Task.Delay(20, timeout.Token); }
            }
            var path = $"/brains/{person.BrainId}/apps/node?kind=confirmation&name={Uri.EscapeDataString(name)}";
            using var response = await person.Http.GetAsync(path, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var view = (await response.Content.ReadFromJsonAsync<UiView>(ct))!;
            var nodes = All(view.Root).ToArray();
            Assert.Contains(nodes, node => node.Text == "Delete report.txt?");
            Assert.Contains(nodes, node => node.Text == "This request expires in 5 minutes.");
            var approve = Assert.Single(nodes, node => node.Kind == UiNodeKind.Button && node.Text == "Delete");
            using var resolved = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
                new { kind = "confirmation", name, revision = view.Generation, field = approve.Key, action = approve.Action, operationId = Guid.NewGuid() }, ct);
            Assert.True(resolved.IsSuccessStatusCode);
            Assert.Equal(ConfirmationOutcome.Approved, (await answer).Outcome);
            using var forged = await person.Http.PostAsJsonAsync($"/brains/{person.BrainId}/apps/event",
                new { kind = "confirmation", name = "another/confirmations", action = approve.Action, operationId = Guid.NewGuid() }, ct);
            Assert.False(forged.IsSuccessStatusCode);
        }
        finally { using (person.AsCaller()) { await inbox.Resolve(id, false); } }
    }

    private static IEnumerable<UiNode> All(UiNode node)
        => new[] { node }.Concat(node.Children.SelectMany(All));
}
