namespace DigitalBrain.Apps.Signals;

// A request a rule refused: published instead of AppRequested, so the request never becomes a fact.
[GenerateSerializer, Alias("apps.app-rejected")]
public sealed record AppRejected([property: Id(0)] string Operation, [property: Id(1)] string Rule, [property: Id(2)] string Reason) : Signal;
