using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

// 06-05b (R4-20): each outgoing call carries the runtime caller exactly as the trusted os stamp stands, so a plain neuron's
// AdmitSynapse judges the os caller and never a runtime caller a call wrote for itself.
[Trait("host", "own")]
public sealed class RuntimeCallerMirrorFacts
{
    [Fact(Timeout = 120_000)]
    public async Task TheTrustedOsStampIsMirroredAsTheRuntimeCallerAndARuntimeCallerWithoutOneIsCleared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var app = brain.Owner.Key("apps/rows");
        try
        {
            using (brain.Owner.AsApp(app))
            {
                RuntimeCallerMirror.Mirror();
                Assert.True(CallerContextStamper.TryGet(out var caller));
                Assert.Equal(RuntimeCallerMirror.Of(caller), RuntimeCallerContext.Current);
                Assert.Equal((brain.Owner.BrainId, app), (RuntimeCallerContext.Current!.BrainId, RuntimeCallerContext.Current.AppId));
            }

            // A caller the flow wrote for itself, with no os stamp: the mirror's keys are the runtime's, and all of them go.
            _ = RuntimeCallerContext.Stamp(new(brain.Owner.PrincipalId, brain.Owner.AccountId, brain.Owner.BrainId, app, "conversation"));
            Assert.All(RuntimeCallerMirror.Keys, key => Assert.NotNull(RequestContext.Get(key)));
            RuntimeCallerMirror.Mirror();
            Assert.Null(RuntimeCallerContext.Current);
            Assert.All(RuntimeCallerMirror.Keys, key => Assert.Null(RequestContext.Get(key)));
        }
        finally { RequestContext.Clear(); }
    }
}
