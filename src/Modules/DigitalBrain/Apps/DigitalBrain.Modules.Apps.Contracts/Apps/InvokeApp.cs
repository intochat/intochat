namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.invoke-app")]
// Id 3 (ExpectedRevision) retired with the packages layer; never reuse it.
public sealed record InvokeApp([property: Id(0)] Guid InvocationId, [property: Id(1)] string Operation, [property: Id(2)] string Input);
