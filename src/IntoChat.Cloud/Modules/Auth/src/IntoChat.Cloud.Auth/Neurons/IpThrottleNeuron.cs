
namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Neuron("auth.ip-throttle")]
internal sealed class IpThrottleNeuron(
    AuthModuleOptions options,
    TimeProvider clock) : AuthNeuron<IpThrottleState>, IIpThrottle
{
    public async Task<ThrottleResult> Hit()
    {
        var now = clock.GetUtcNow();
        var recent = Snapshot.Hits.Where(at => at > now - options.IpThrottleWindow).ToArray();
        if (recent.Length >= options.IpThrottleLimit) { return new ThrottleResult(false, recent[0] + options.IpThrottleWindow); }
        await SaveQuietly(new IpThrottleState { Hits = [.. recent, now] });
        return new ThrottleResult(true, null);
    }
}

// IP prefixes are purpose-hashed before reaching the neuron. A bounded sliding window, never exported.
internal sealed class IpThrottleState
{
    public DateTimeOffset[] Hits { get; set; } = [];
}
