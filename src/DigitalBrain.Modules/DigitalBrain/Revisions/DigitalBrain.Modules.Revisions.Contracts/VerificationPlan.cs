namespace DigitalBrain.Revisions;

[GenerateSerializer, Alias("revisions.expectation")]
public sealed record NeuronExpectation(
    [property: Id(0)] string Contract,
    [property: Id(1)] string Key,
    [property: Id(2)] string Method,
    [property: Id(3)] string Arguments,
    [property: Id(4)] string Expected);

// Written by the change's author: what to ask the child and what it must answer. The budget is
// the plan's, never a global timeout.
[GenerateSerializer, Alias("revisions.plan")]
public sealed record VerificationPlan(
    [property: Id(0)] NeuronExpectation[] Calls,
    [property: Id(1)] string[] HealthyResources,
    [property: Id(2)] string[] Spans,
    [property: Id(3)] TimeSpan Budget,
    [property: Id(4)] string AspireKey = "DigitalBrain.OS");

[GenerateSerializer, Alias("revisions.check")]
public sealed record VerificationCheck([property: Id(0)] string Name, [property: Id(1)] bool Passed, [property: Id(2)] string Detail);

[GenerateSerializer, Alias("revisions.report")]
public sealed record VerificationReport([property: Id(0)] VerificationCheck[] Checks, [property: Id(1)] TimeSpan Elapsed)
{
    public bool Passed => Checks.All(check => check.Passed);
    public string? FirstFailure => Checks.FirstOrDefault(check => !check.Passed)?.Name;
}