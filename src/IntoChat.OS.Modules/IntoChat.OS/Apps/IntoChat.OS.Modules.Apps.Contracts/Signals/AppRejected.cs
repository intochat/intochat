namespace IntoChat.OS.Apps.Signals;

// A request a rule refused: published instead of AppRequested, so the request never becomes a fact.
// The verdict names the rule and its reason.
[GenerateSerializer, Alias("apps.app-rejected")]
public sealed record AppRejected([property: Id(0)] string Operation, [property: Id(1)] Verdict Verdict) : Signal
{
    public string Rule => Verdict.Rule;
    public string Reason => Verdict.Reason;
}
