namespace DigitalBrain;

// A rule: a behavior that judges instead of acts. A shared class in an app bundle, pure and without I/O;
// the app runs every rule whose TSignal matches before a fact is published, and any rejection refuses it.
public interface ILogic<TSignal> where TSignal : Signal
{
    Verdict Validate(TSignal signal);
}

// Rule names the rule that judged; the sandbox stamps it, a rule never sets it.
[GenerateSerializer, Alias("brain.verdict")]
public sealed record Verdict([property: Id(0)] bool Allowed, [property: Id(1)] string Reason)
{
    [Id(2)] public string Rule { get; init; } = "";

    public static Verdict Allow() => new(true, "");
    public static Verdict Reject(string reason) => new(false, reason);
}
