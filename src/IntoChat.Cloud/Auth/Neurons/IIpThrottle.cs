using DigitalBrain.Access;
using IntoChat.OS;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Contracts.NeuronAlias("auth.ip-throttle"), DigitalBrain.Contracts.DefaultNeuronType("auth.ip-throttle")]
public interface IIpThrottle
{
    const string Type = "auth.ip-throttle";

    static string Key(string ipHash) => $"{NeuronKeys.SystemShard(ipHash)}/auth/ip/{ipHash}";

    [Internal]
    Task<ThrottleResult> Hit();
}

public sealed record ThrottleResult(bool Allowed, DateTimeOffset? RetryAt);
