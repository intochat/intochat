using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Modules.AI.Workflows;
using Microsoft.Agents.AI.Workflows;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// Phase 4b spike: an Agent Framework workflow that waits for a person survives a restart when its checkpoints live in neuron state.
// The run is thrown away while it waits; a new workflow resumes from the stored checkpoints alone and finishes after the answer.
public sealed class WorkflowCheckpointSpikeFacts
{
    // A hang fails the fact instead of blocking the run.
    private static readonly CancellationTokenSource Limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    private static CancellationToken Ct => Limit.Token;

    private static Workflow Build() => ApprovalDemoWorkflow.Build();

    [Fact]
    public async Task AWaitingWorkflowResumesFromStateCheckpointsAfterARestart()
    {
        Limit.CancelAfter(TimeSpan.FromSeconds(60));
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        ExternalRequest? asked = null;
        CheckpointInfo? last = null;

        await using (var run = await InProcessExecution.RunStreamingAsync(Build(), "hello", CheckpointManager.CreateJson(new StateCheckpointStore(state)), "run-1", Ct))
        {
            // Ends when the workflow halts on its pending approval instead of waiting for the answer.
            await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: false, Ct))
            {
                if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } checkpoint) { last = checkpoint; }
                if (evt is RequestInfoEvent request) { asked = request.Request; }
            }
        }

        Assert.NotNull(asked);
        Assert.Equal("HELLO", asked.Data.As<string>());
        Assert.NotNull(last);
        Assert.NotEmpty(state);

        // "Restart": nothing from the first run is kept but the stored checkpoint texts.
        var restored = new Dictionary<string, string>(state, StringComparer.Ordinal);
        string? output = null;
        await using (var resumed = await InProcessExecution.ResumeStreamingAsync(Build(), last, CheckpointManager.CreateJson(new StateCheckpointStore(restored)), Ct))
        {
            await resumed.SendResponseAsync(asked.CreateResponse(true));
            await foreach (var evt in resumed.WatchStreamAsync(Ct))
            {
                if (evt is WorkflowOutputEvent done) { output = done.As<string>(); }
            }
        }

        Assert.Equal("written", output);
    }
}
