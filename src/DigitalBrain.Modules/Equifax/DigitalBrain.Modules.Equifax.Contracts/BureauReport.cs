namespace DigitalBrain.Equifax;

// RawJson is the provider's payload, truncated to 32 KB.
[GenerateSerializer, Alias("equifax.bureau-report")]
public sealed record BureauReport(
    [property: Id(0)] string DocumentNumber,
    [property: Id(1)] DateTimeOffset ReadAt,
    [property: Id(2)] string RawJson,
    [property: Id(3)] BureauFlags Flags);

// The flags the SV loan scope reads; each is null when the payload does not carry it.
[GenerateSerializer, Alias("equifax.bureau-flags")]
public sealed record BureauFlags(
    [property: Id(0)] bool? InfocorpNegative,
    [property: Id(1)] string? SbsRating,
    [property: Id(2)] int? MaxDaysDelay,
    [property: Id(3)] bool? SicomUnpaidDebts,
    [property: Id(4)] bool? HasMortgage,
    [property: Id(5)] bool? HasCarLoan);
