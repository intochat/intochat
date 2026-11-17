using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Testing;
using Xunit;

namespace IntoChat.OS.Modules.Google.Gmail;

// The Gmail push webhook and lifecycle reminder run through IBrainOwnerDispatch, which now stamps the brain's scheduler (owner
// decision 3, 17 Nov) inside a trace it began for the directory's brain owner. Gmail needs a person only to name who pays for the
// Google calls; credentials are opened by the brain sealer, which reads no caller. So the scheduler pays as that trace's owner.
public sealed class GmailResumedWorkPayerFacts
{
    private const string Brain = "owner";

    [Fact]
    public async Task ThePersonPaysForTheirOwnCalls()
    {
        await using var os = TestOS.Local();
        var caller = os.Edge.Person("ann", "acct-ann", new BrainId(Brain));

        Assert.Equal("acct-ann", GmailNeuron.PayerOf(caller, null, Brain));
    }

    [Fact]
    public async Task TheSchedulerPaysAsTheOwnerTraceTheDispatchBegan()
    {
        await using var os = TestOS.Local();
        var scheduler = os.Edge.Scheduler(new BrainId(Brain));
        var dispatched = TraceContext.Begin("acct-owner", Brain, TraceContext.OriginOf(Brain, Brain + "/gmail-push"));

        Assert.Equal("acct-owner", GmailNeuron.PayerOf(scheduler, dispatched, Brain));
    }

    [Fact]
    public async Task TheSchedulerWithoutTheDispatchTraceIsRefused()
    {
        await using var os = TestOS.Local();
        var scheduler = os.Edge.Scheduler(new BrainId(Brain));
        var foreign = TraceContext.Begin("acct-other", "other", TraceContext.OriginOf("other", "other/gmail-push"));

        Assert.Throws<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(scheduler, null, Brain));
        Assert.Throws<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(scheduler, foreign, Brain));
    }

    [Fact]
    public async Task AModuleOrTheKernelDoesNotPayForGmail()
    {
        await using var os = TestOS.Local();
        var dispatched = TraceContext.Begin("acct-owner", Brain, TraceContext.OriginOf(Brain, Brain + "/gmail-push"));

        Assert.Throws<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Module("gmail", new BrainId(Brain)), dispatched, Brain));
        Assert.Throws<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Kernel(new BrainId(Brain)), dispatched, Brain));
    }
}
