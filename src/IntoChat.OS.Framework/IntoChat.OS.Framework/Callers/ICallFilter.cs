using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

[GenerateSerializer, Alias("enforcement.call-decision")]
public sealed record CallDecision
{
    [Id(0)] public bool Allowed { get; init; }
    [Id(1)] public CallDenial? Denial { get; init; }
    [Id(2)] public string? Explanation { get; init; }
    // An allowed decision that rests on a Once grant: the machine spends it through a signed consume (R68).
    [Id(3)] public bool ConsumesOnce { get; init; }

    public static CallDecision Allow() => new() { Allowed = true };

    public static CallDecision Deny(CallDenial denial, string explanation) =>
        new() { Allowed = false, Denial = denial, Explanation = explanation };
}

// The one enforcement point: grants (P2), then allowances and limits (P3), then the broker gateway (P4).
public interface ICallFilter
{
    ValueTask<CallDecision> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken = default);
}
