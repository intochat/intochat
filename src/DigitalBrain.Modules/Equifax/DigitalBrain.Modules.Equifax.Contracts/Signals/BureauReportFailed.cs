using DigitalBrain;

namespace DigitalBrain.Equifax.Signals;

// Detail is the provider status, never a secret.
[GenerateSerializer, Alias("equifax.bureau-report-failed")]
public sealed record BureauReportFailed([property: Id(0)] string DocumentNumber, [property: Id(1)] string Detail) : Signal;
