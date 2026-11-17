using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Access;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

// Stamps the kernel on the in-process silo's caller ambient, as EdgeAuthority.EnterKernel does. A fact that drives the
// runtime's own machinery (hosting, journals, deliveries, operations) runs as the kernel; a fact about a person or an app
// stamps that caller through the silo's EdgeAuthority instead.
internal static class TestStamp
{
    public static IDisposable Kernel() => OrleansCallerAmbient.Instance.Enter(Caller.OfKernel(BrainId.Kernel));
}
