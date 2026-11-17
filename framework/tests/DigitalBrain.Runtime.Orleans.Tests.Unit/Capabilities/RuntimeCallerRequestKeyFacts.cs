using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Capabilities;

// A caller is the stamp the kernel carries, never strings someone set on the request context.
public sealed class RuntimeCallerRequestKeyFacts
{
    [Fact]
    public void CurrentIsNullWhenOnlyRequestContextKeysAreSet()
    {
        RequestContext.Clear();
        try
        {
            RequestContext.Set("db.cap.principal", "mallory");
            RequestContext.Set("db.cap.account", "mallory");
            RequestContext.Set("db.cap.brain", "victim");
            RequestContext.Set("db.cap.app", "platform");

            Assert.Null(Stamps.Current);
        }
        finally
        {
            RequestContext.Clear();
        }
    }
}
