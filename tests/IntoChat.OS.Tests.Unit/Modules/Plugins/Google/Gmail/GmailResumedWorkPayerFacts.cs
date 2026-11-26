using DigitalBrain;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Testing;
using Xunit;

namespace IntoChat.OS.Modules.Google.Gmail;

// The Gmail push webhook and lifecycle reminder run through IBrainOwnerDispatch as the brain's scheduler (owner decision 3, 17 Nov).
// Gmail needs a person only to name who pays for the Google calls (credentials are opened by the brain sealer, which reads no
// caller). For the scheduler the payer is the brain's owner as the directory records it (BrainOwners.VerifiedOwnerAsync), never the
// ambient trace, which any in-process code can set.
public sealed class GmailResumedWorkPayerFacts
{
    private const string Brain = "owner";

    private static Func<string, Task<string?>> Directory(string? owner) => brain => Task.FromResult(brain == Brain ? owner : null);

    [Fact]
    public async Task ThePersonPaysForTheirOwnCalls()
    {
        await using var os = TestOS.Local();
        var caller = os.Edge.Person("ann", "acct-ann", new BrainId(Brain));

        Assert.Equal("acct-ann", await GmailNeuron.PayerOf(caller, Brain, Directory("acct-owner")));
    }

    [Fact]
    public async Task TheSchedulerPaysAsTheDirectorysBrainOwner()
    {
        await using var os = TestOS.Local();

        Assert.Equal("acct-owner", await GmailNeuron.PayerOf(os.Edge.Scheduler(new BrainId(Brain)), Brain, Directory("acct-owner")));
    }

    [Fact]
    public async Task AForgedTracePayerIsIgnored()
    {
        await using var os = TestOS.Local();
        var forged = TraceContext.Begin("acct-mallory", Brain, TraceContext.OriginOf(Brain, Brain + "/gmail-push"));

        using (TraceContext.Enter(forged))
        {
            Assert.Equal("acct-owner", await GmailNeuron.PayerOf(os.Edge.Scheduler(new BrainId(Brain)), Brain, Directory("acct-owner")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Scheduler(new BrainId(Brain)), Brain, Directory(null)));
        }
    }

    [Fact]
    public async Task TheSchedulerOfAnotherBrainOrAnUnownedBrainIsRefused()
    {
        await using var os = TestOS.Local();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Scheduler(new BrainId("other")), Brain, Directory("acct-owner")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Scheduler(new BrainId(Brain)), Brain, Directory(null)));
    }

    [Fact]
    public async Task AModuleOrTheKernelDoesNotPayForGmail()
    {
        await using var os = TestOS.Local();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Module("gmail", new BrainId(Brain)), Brain, Directory("acct-owner")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GmailNeuron.PayerOf(os.Edge.Kernel(new BrainId(Brain)), Brain, Directory("acct-owner")));
    }
}
