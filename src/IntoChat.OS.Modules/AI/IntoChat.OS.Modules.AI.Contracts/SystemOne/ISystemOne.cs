using Orleans.Concurrency;

namespace IntoChat.OS.AI.SystemOne;

// A System One model (TypeSafe Jev): state in, typed decisions out. Not a chat model: it never
// generates strings, so it cannot be asked through ILLM. It decides, and code branches on the answer.
[Alias("ai.systemone"), Orleans.Metadata.DefaultGrainType("ai.systemone")]
public interface ISystemOne : INeuron
{
    [AlwaysInterleave] Task<SystemOneResult> Ask(SystemOneRequest request, CancellationToken ct = default);
}
