namespace DigitalBrain.Compute;

public static class ComputeUnits
{
    public const decimal PerUsd = 100m;

    public static decimal ToUsd(decimal compute) => compute / PerUsd;
}

public interface IPriceBook
{
    string Version { get; }

    decimal PriceInCompute(string meterId, decimal quantity);
}
