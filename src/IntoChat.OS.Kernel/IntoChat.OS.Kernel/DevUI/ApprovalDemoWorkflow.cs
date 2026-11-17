using Microsoft.Agents.AI.Workflows;

namespace IntoChat.OS.Kernel.DevUI;

// The smallest workflow that waits for a person: shout the input, ask for approval through a request port, then finish. It is the
// phase 4b spike's subject and DevUI's sample of a workflow entity in Development.
internal static class ApprovalDemoWorkflow
{
    public const string Name = "approval-demo";

    public static Workflow Build()
    {
        var shout = ((Func<string, string>)(text => text.ToUpperInvariant())).BindAsExecutor("shout");
        var approval = RequestPort.Create<string, bool>("approval");
        var finish = ((Func<bool, string>)(approved => approved ? "written" : "declined")).BindAsExecutor("finish");
        return new WorkflowBuilder(shout).WithName(Name).AddEdge(shout, approval).AddEdge(approval, finish).WithOutputFrom(finish).Build();
    }
}
