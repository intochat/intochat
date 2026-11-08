using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Flows.FlowTypeFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class PrFlowFacts
{
    [Fact]
    public async Task PrWaitsUntilMerged()
    {
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);
        var repository = new NeuronId(runtime.Brain, "github", "repo");
        var inner = new PrInner { Repository = repository.ToString(), Branch = "feature", Title = "Add notes", Body = "Notes." };

        var id = await flows.Start(PrFlow.Alias, "Add notes", null, JsonSerializer.Serialize(inner, PrFlow.Json), Ct);

        var waiting = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting);
        Assert.Equal(["open"], waiting.Done);
        Assert.Equal(("merged", WaitReason.Schedule), (waiting.WaitingStep, waiting.WaitingFor));
        var held = JsonSerializer.Deserialize<PrInner>((await runtime.Get<IFlowStarter>(Id(id)).Head()).InnerJson, PrFlow.Json)!;
        Assert.Equal(StubRepository.Number, held.PullRequestNumber);
        var script = runtime.Get<IRepositoryScript>(repository);
        Assert.Equal(1, await script.Created());

        await script.Merge(StubRepository.Number);

        var done = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Completed);
        Assert.Equal(["open", "merged"], done.Done);
        Assert.Equal(1, await script.Created());
    
    }

    [Fact]
    public async Task MergedWaitReconcilesOnSchedule()
    {
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);
        var repository = new NeuronId(runtime.Brain, "github", "quiet");
        var inner = new PrInner { Repository = repository.ToString(), Branch = "feature", Title = "Quiet", PollEvery = TimeSpan.FromSeconds(3) };
        var id = await flows.Start(PrFlow.Alias, "Quiet", null, JsonSerializer.Serialize(inner, PrFlow.Json), Ct);
        await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting);

        await runtime.Get<IRepositoryScript>(repository).MergeQuietly();

        var done = await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Completed);
        Assert.Equal(["open", "merged"], done.Done);
    }

    [Fact]
    public async Task ClosedWithoutMergeFailsTheFlow()
    {
        await using var fixture = new FlowTypesFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        var flows = Index(runtime);
        var repository = new NeuronId(runtime.Brain, "github", "closed");
        var inner = new PrInner { Repository = repository.ToString(), Branch = "feature", Title = "Closed" };
        var id = await flows.Start(PrFlow.Alias, "Closed", null, JsonSerializer.Serialize(inner, PrFlow.Json), Ct);
        await Eventually(() => flows.Read(id, Ct), v => v.Status == FlowStatus.Waiting);

        await runtime.Get<IRepositoryScript>(repository).Close(StubRepository.Number);

        var failed = await Eventually(() => flows.Read(id, Ct), v => v.Status is FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Equal("closed without merge", failed.Detail);
    }
}
