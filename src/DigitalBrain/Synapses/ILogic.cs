namespace DigitalBrain;

// A rule: a behavior that judges instead of acts. A shared class in an app bundle, pure and without I/O;
// the app runs every rule whose TSignal matches before a fact is published, and any rejection refuses it.
public interface ILogic<TSignal> where TSignal : Signal
{
    Verdict Validate(TSignal signal);
}

// Rule names the rule that judged; the sandbox stamps it, a rule never sets it.
public sealed record Verdict(bool Allowed, string Reason)
{
    public string Rule { get; init; } = "";

    public static Verdict Allow() => new(true, "");
    public static Verdict Reject(string reason) => new(false, reason);
}
