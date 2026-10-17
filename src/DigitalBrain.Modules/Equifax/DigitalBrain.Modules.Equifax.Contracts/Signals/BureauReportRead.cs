using DigitalBrain;

namespace DigitalBrain.Equifax.Signals;

[GenerateSerializer, Alias("equifax.bureau-report-read")]
public sealed record BureauReportRead([property: Id(0)] BureauReport Report) : Signal;
