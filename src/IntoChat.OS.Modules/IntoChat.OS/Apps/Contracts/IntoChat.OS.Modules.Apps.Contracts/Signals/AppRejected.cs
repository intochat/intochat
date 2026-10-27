namespace IntoChat.OS.Modules.Apps.Signals;

// A request a rule refused: published instead of AppRequested, so the request never becomes a fact.
// The verdict names the rule and its reason.
[DigitalBrain.Signal("apps.app-rejected")]
public sealed record AppRejected(string Operation, Verdict Verdict) : DigitalBrain.Signal
{
    public string Rule => Verdict.Rule;
    public string Reason => Verdict.Reason;
}
