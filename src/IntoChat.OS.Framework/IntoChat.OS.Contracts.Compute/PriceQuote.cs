namespace IntoChat.OS.Framework.Compute;

public enum PricingStatus
{
    Unknown = 0,
    Unpriced = 1,
    Known = 2,
}

public enum ExternalChargePolicy
{
    Unspecified = 0,
    EveryDispatchedAttempt = 1,
    SuccessfulResponse = 2,
    Informational = 3,
}

[GenerateSerializer, Alias("compute.price-quote")]
public sealed record PriceQuote
{
    [Id(0)] public required string Unit { get; init; }
    [Id(1)] public long? MicrosPerUnit { get; init; }
    [Id(2)] public string? TariffVersion { get; init; }
    [Id(3)] public DateTimeOffset? EffectiveFrom { get; init; }
    [Id(4)] public string? Source { get; init; }
    [Id(5)] public ExternalChargePolicy ChargePolicy { get; init; }
    public PricingStatus Status => MicrosPerUnit is null ? PricingStatus.Unpriced : PricingStatus.Known;
}

public sealed record ExternalOperationDescriptor(string Name, string Integration, string Unit);
