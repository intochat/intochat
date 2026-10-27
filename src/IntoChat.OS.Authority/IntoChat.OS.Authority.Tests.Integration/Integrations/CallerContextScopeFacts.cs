using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Authority.Enforcement;

namespace IntoChat.OS.Authority.Tests.Integration.Integrations;

public sealed class CallerContextScopeFacts
{
    [Fact]
    public void ScopeRestoresPreviousValuesAndRemovesAbsentValuesEvenOnException()
    {
        var caller = new CallerContext { PrincipalId = "platform", AccountId = "platform", BrainId = "test", Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform };
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var outer = CallerContextScope.Scoped(caller);
                using (CallerContextScope.Scoped(caller with { BrainId = "nested" }))
                { Assert.Equal("nested", CallerContextStamper.Require().BrainId); }
                Assert.Same(caller, CallerContextStamper.Require());
                throw new InvalidOperationException("restore");
            }));
            Assert.Null(RequestContext.Get(CallerContextStamper.RequestContextKey));
            var previous = new object();
            RequestContext.Set(CallerContextStamper.RequestContextKey, previous);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var scope = CallerContextScope.Scoped(caller);
                Assert.Same(caller, CallerContextStamper.Require());
                throw new InvalidOperationException("restore previous");
            }));
            Assert.Same(previous, RequestContext.Get(CallerContextStamper.RequestContextKey));
            Assert.False(typeof(CallerContextScope).IsVisible);
        }
        finally { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }
}
