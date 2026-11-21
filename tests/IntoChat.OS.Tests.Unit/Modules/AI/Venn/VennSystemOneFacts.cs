using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Venn;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Venn;

public sealed class VennSystemOneFacts
{
    [Fact]
    public async Task VennSystemOneAbstainsColdLearnsFromOutcomesAndRoutesFast()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AIModule).Assembly);

        var venn = os.Get<IVennSystemOne>(new NeuronId(new BrainId("test"), "ai.venn", "ino"));

        // 1. Cold start abstains honestly (escalates to System 2 LLM).
        var cold = await venn.Choose([1f, 0f, 0f, 0f]);
        Assert.True(cold.Abstained);
        Assert.Null(cold.ChosenAction);

        // 2. Record personal precedents (auth questions -> open Identity tree; photo edits -> PhotoCraft).
        await venn.RecordOutcome("workspace", "where is auth handled?", [0.95f, 0.05f, 0f, 0f], "workspace.openTree:Identity", outcomeScore: 1.0);
        await venn.RecordOutcome("workspace", "why am I getting 401?", [0.92f, 0.08f, 0f, 0f], "workspace.openTree:Identity", outcomeScore: 0.9);
        await venn.RecordOutcome("studio", "remove background from portrait", [0f, 0.96f, 0.04f, 0f], "artcraft.photocraft", outcomeScore: 1.0);

        var compiled = await venn.CompileHead(abstentionThreshold: 0.65);
        Assert.Equal(2, compiled.Prototypes.Length);
        Assert.Equal(3, compiled.Precedents.Length);

        // 3. System 1 Choice routes similar auth query in <1ms without LLM.
        var authChoice = await venn.Choose([0.94f, 0.06f, 0f, 0f]);
        Assert.False(authChoice.Abstained);
        Assert.Equal("workspace.openTree:Identity", authChoice.ChosenAction);
        Assert.True(authChoice.Confidence > 0.95);

        // 4. Out-of-distribution query abstains cleanly -> System 2 fallback!
        var novel = await venn.Choose([0f, 0f, 0f, 1f]);
        Assert.True(novel.Abstained);
        Assert.Null(novel.ChosenAction);

        // 5. Continuous Score & Noul (yes/no) predictions work on the same compiled head.
        var score = await venn.Score([0.93f, 0.07f, 0f, 0f]);
        Assert.False(score.Abstained);
        Assert.True(score.PredictedScore > 0.9);

        var noul = await venn.Noul([0f, 0.95f, 0.05f, 0f], "artcraft.photocraft");
        Assert.False(noul.Abstained);
        Assert.True(noul.Positive);
        Assert.True(noul.Probability > 0.9);
    }
}
