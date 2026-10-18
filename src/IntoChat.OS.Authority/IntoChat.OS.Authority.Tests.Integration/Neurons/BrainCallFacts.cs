using IntoChat.OS.Authority.Neurons;
using Grpc.Core;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

public sealed class BrainCallFacts
{
    private static string? Trailer(RpcException error, string key) => error.Trailers.GetValue(key);

    [Fact]
    public void ARevisionConflictIsAbortedWithTheCurrentRevision()
    {
        var translated = BrainCall.Translate(new RevisionConflictException("Stale revision.", 42));
        Assert.Equal(StatusCode.Aborted, translated.StatusCode);
        Assert.Equal("REVISION_CONFLICT", Trailer(translated, BrainCall.ReasonTrailer));
        Assert.Equal(BrainCall.ErrorDomain, Trailer(translated, BrainCall.DomainTrailer));
        Assert.Equal("42", Trailer(translated, BrainCall.CurrentRevisionTrailer));
    }

    [Fact]
    public void AJournalConflictIsAFailedPreconditionWithoutItsMessage()
    {
        var translated = BrainCall.Translate(new JournalConflictException("machine-7 wrote part 3"));
        Assert.Equal(StatusCode.FailedPrecondition, translated.StatusCode);
        Assert.Equal("JOURNAL_DIVERGED", Trailer(translated, BrainCall.ReasonTrailer));
        Assert.DoesNotContain("machine-7", translated.Status.Detail);
    }

    [Fact]
    public void AStaleActionIsAFailedPreconditionWithItsReason()
    {
        var translated = BrainCall.Translate(new StaleActionException("This action is no longer available."));
        Assert.Equal(StatusCode.FailedPrecondition, translated.StatusCode);
        Assert.Equal("STALE_ACTION", Trailer(translated, BrainCall.ReasonTrailer));
        Assert.Equal("This action is no longer available.", translated.Status.Detail);
    }

    [Fact]
    public void OtherInvalidOperationsStayInternal()
    {
        var translated = BrainCall.Translate(new InvalidOperationException("boom"));
        Assert.Equal(StatusCode.Internal, translated.StatusCode);
        Assert.Null(Trailer(translated, BrainCall.ReasonTrailer));
    }
}
