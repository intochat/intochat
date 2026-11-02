using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Authority.Identity.Handles;

[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
public interface IHandleDirectory
{
    const string TypeName = "identity.handles";
    static string Key(string handle) => $"{HandleShard.BrainOf(Normalize(handle))}/handles/{Normalize(handle)}";
    static string Normalize(string handle) => handle.Trim().ToLowerInvariant();
    static bool IsValid(string handle) => Regex.IsMatch(handle, "^[a-z0-9][a-z0-9-]{2,31}$");
    Task<bool> Claim(string brainId);            // false if already claimed by another brain
    Task Release(string brainId);                // no-op unless owner matches
    [DigitalBrain.ReadOnlyCall] Task<string?> Resolve();      // brain id or null
}

[GenerateSerializer, Alias("identity.handle-state")]
public sealed record HandleState
{
    [Id(0)] public string BrainId { get; init; } = "";
    [Id(1)] public DateTimeOffset ClaimedAt { get; init; }
}
