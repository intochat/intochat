using DigitalBrain.Capabilities.Compute;

namespace DigitalBrain.Testing;

/// <summary>
/// TestOS's ambient cause, the same rule as the host runtime: a save inside a reaction takes the cause's trace id and its
/// <see cref="Causation.Address"/> (also in neurons the reaction calls, which run in its flow); outside one, the request's
/// <see cref="TraceContext"/> trace id; with neither, a fresh id.
/// </summary>
internal static class TestCause
{
    private static readonly AsyncLocal<(string TraceId, string CausationId)?> Current = new();

    // The fresh trace id minted for this async flow when it had no cause and no request trace, kept so every later stamp agrees.
    private static readonly AsyncLocal<string?> Fresh = new();

    /// <summary>Makes the signal <paramref name="publisher"/> journaled at <paramref name="sequence"/> the cause of this flow.</summary>
    public static void Enter(NeuronId publisher, long sequence, string? traceId)
        => Current.Value = (traceId ?? Request() ?? Fresh.Value ?? Guid.NewGuid().ToString("N"), Causation.Address(publisher, sequence));

    /// <summary>The trace and causation ids the signals of a save made now are journaled with.</summary>
    public static (string TraceId, string? CausationId) Stamp()
        => Current.Value is { } cause ? cause : (Request() ?? (Fresh.Value ??= Guid.NewGuid().ToString("N")), null);

    private static string? Request() => TraceContext.Current?.TraceId;
}
