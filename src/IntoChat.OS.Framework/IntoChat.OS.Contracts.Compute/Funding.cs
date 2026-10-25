namespace IntoChat.OS.Contracts.Compute;

public enum FundingKind
{
    Local = 0,
    OwnConnection = 1,
    Integration = 2,
    Circle = 3,
}

// The selected connection determines funding; these values never establish caller authority.
[GenerateSerializer, Alias("compute.funding")]
public readonly record struct Funding(
    [property: Id(0)] FundingKind Kind,
    [property: Id(1)] string? CircleKey = null)
{
    public static Funding Local => new(FundingKind.Local);
    public static Funding OwnConnection => new(FundingKind.OwnConnection);
    public static Funding Integration => new(FundingKind.Integration);

    public static Funding Circle(string circleKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(circleKey);
        return new(FundingKind.Circle, circleKey);
    }
}

[GenerateSerializer, Alias("compute.cost-subject")]
public sealed record CostSubject(
    [property: Id(0)] string? Synapse = null,
    [property: Id(1)] string? App = null,
    [property: Id(2)] string? Neuron = null);
