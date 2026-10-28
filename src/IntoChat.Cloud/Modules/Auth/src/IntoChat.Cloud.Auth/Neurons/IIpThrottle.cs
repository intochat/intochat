using IntoChat.Cloud.Mail;
using DigitalBrain.Access;

namespace IntoChat.Cloud.Auth.Neurons;

[DigitalBrain.Contracts.NeuronAlias("auth.ip-throttle"), DigitalBrain.Contracts.DefaultNeuronType("auth.ip-throttle")]
public interface IIpThrottle
{
    const string Type = "auth.ip-throttle";

    static string Key(string ipHash) => $"{SystemShard.BrainOf(ipHash)}/auth/ip/{ipHash}";

    [Internal]
    Task<ThrottleResult> Hit();
}

public sealed record ThrottleResult(bool Allowed, DateTimeOffset? RetryAt);
