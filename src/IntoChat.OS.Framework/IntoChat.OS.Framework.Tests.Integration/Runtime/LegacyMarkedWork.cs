using Orleans.Concurrency;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime.Legacy;

// The contract of NeutralMarkerFacts as today's contracts declare it: Orleans attributes on a grain interface
// of the same name, so Orleans computes the invokable ids the neutral shape must keep.
public interface IMarkedWork : IGrainWithStringKey
{
    Task Hold();
    Task<int> Count();
    [AlwaysInterleave] Task<int> Peek();
    [ReadOnly] Task<int> ReadCount();
    Task<string> Probe(string mode);
    [OneWay] Task Post(int count);
    [ResponseTimeout("00:00:00.500")] Task Stall();
}
