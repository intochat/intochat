using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Mcp.Tools;
using Xunit;

namespace IntoChat.OS.Tests.Kernel;

// MCP stamps the agent. A confirmation card is the person's to answer (owner decision 3, 17 Nov): screen_approve refuses an inbox
// item for the agent up front rather than calling Resolve, where the inbox would refuse a person-only plan and turn any other
// answer from an agent into a self-approval denial.
public sealed class ScreenApproveAnswererFacts
{
    private static readonly BrainId Brain = new("owner");

    [Fact]
    public async Task OnlyTheDirectPersonAnswersAConfirmation()
    {
        await using var os = TestOS.Local();
        var person = os.Edge.Person("ann", "acct", Brain);
        var agent = os.Edge.Agent(new DigitalBrain.Access.Actor.Person("ann", "acct"), Brain, "chat-1");

        Assert.True(ScreenTools.MayAnswerConfirmation(person));
        Assert.False(ScreenTools.MayAnswerConfirmation(agent));
        Assert.False(ScreenTools.MayAnswerConfirmation(os.Edge.Person("ann", "acct", Brain, "chat-1")));
        Assert.False(ScreenTools.MayAnswerConfirmation(os.Edge.App("app.settings", new DigitalBrain.Access.Actor.Person("ann", "acct"), Brain)));
    }

    [Fact]
    public async Task TheRuleIsTheInboxAnswererRule()
    {
        await using var os = TestOS.Local();
        foreach (var caller in new[]
        {
            os.Edge.Person("ann", "acct", Brain),
            os.Edge.Agent(new DigitalBrain.Access.Actor.Person("ann", "acct"), Brain),
            os.Edge.RelayedPerson("ann", "acct", Brain),
            os.Edge.Scheduler(Brain),
        })
        {
            Assert.Equal(Answerer.Of(caller, Brain).IsPerson, ScreenTools.MayAnswerConfirmation(caller));
        }
    }
}
