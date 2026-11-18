using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// A workflow run is a neuron: it halts on its request port as a person-only confirmation, keeps waiting across a restart, and
// finishes from its stored checkpoints once the person answers.
public sealed class WorkflowRunFacts
{
    private static readonly BrainId Brain = new("owner");

    private static TestOS NewOs()
    {
        var services = new ServiceCollection()
            .AddSingleton(new WorkflowDefinition(ApprovalDemoWorkflow.Name, "demo", ApprovalDemoWorkflow.Build))
            .BuildServiceProvider();
        var os = TestOS.Local(options => options.Services = services);
        os.Register(typeof(AIModule).Assembly);
        return os;
    }

    private static IWorkflowRun Run(TestOS os, string key) => os.Get<IWorkflowRun>(new NeuronId(Brain, "workflow.run", key));

    [Fact]
    public async Task ARunWaitsForAPersonAndFinishesAfterApprovalAcrossARestart()
    {
        await using var os = NewOs();
        var started = await Run(os, "workflows/r1").Start(ApprovalDemoWorkflow.Name, "write the report");
        Assert.Equal(WorkflowRunStatus.Waiting, started.Status);
        Assert.Equal(["shout", "approval"], started.Steps);
        var waiting = Assert.Single(os.Confirmations.Waiting, plan => plan.PlanId == started.PlanId);
        Assert.True(waiting.Plan.RequiresPerson);
        Assert.Contains("WRITE THE REPORT", waiting.Plan.Body, StringComparison.Ordinal);

        await os.Restart();
        await os.Confirmations.Approve(started.PlanId!);
        await os.Idle();

        var done = await Run(os, "workflows/r1").Read();
        Assert.Equal(WorkflowRunStatus.Completed, done!.Status);
        Assert.Equal("written", done.Output);
        Assert.Equal(["shout", "approval", "finish"], done.Steps);
    }

    [Fact]
    public async Task ADeclinedRunFinishesWithItsDeclinedBranch()
    {
        await using var os = NewOs();
        var started = await Run(os, "workflows/r2").Start(ApprovalDemoWorkflow.Name, "x");
        await os.Confirmations.Deny(started.PlanId!);
        await os.Idle();
        Assert.Equal("declined", (await Run(os, "workflows/r2").Read())!.Output);
    }

    [Fact]
    public async Task AnUnknownWorkflowIsRefused()
    {
        await using var os = NewOs();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Run(os, "workflows/r3").Start("nope", "x"));
    }
}
