using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.Tests.Support;

[State("unit.limit_exact")]
public sealed record ExactLimitUnitState(string Data)
{
    public ExactLimitUnitState() : this(string.Empty) { }
}

public static class ExactLimitUnit
{
    [Synapse("unit.limit_exact")]
    public static View Show(ExactLimitUnitState _)
        => new("unit.limit_exact", new Button("apply", "Apply", new Emit("unit.limit_exact", "unit.limit_exact.set", JsonSerializer.SerializeToElement(new { }))));

    [Synapse("unit.limit_exact.init", "unit.limit_exact")]
    public static ExactLimitUnitState Init(SignalEnvelope _, ExactLimitUnitState state)
        => state with { Data = "init" };

    [Synapse("unit.limit_exact.set", "unit.limit_exact")]
    public static ExactLimitUnitState Set(SignalEnvelope signal, ExactLimitUnitState state)
        => signal.Payload.ValueKind == JsonValueKind.Object
           && signal.Payload.TryGetProperty("value", out var raw)
           && int.TryParse(raw.GetString(), out var length)
            ? state with { Data = new string('A', length) }
            : state;
}
