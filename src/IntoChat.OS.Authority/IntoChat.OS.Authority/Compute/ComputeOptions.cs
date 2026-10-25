using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Authority.Compute;

public sealed class ComputeOptions : IModuleOptions
{
    public const string Key = "IntoChatOS:Platform:Compute";
    public TimeSpan SettledRetention { get; set; } = TimeSpan.FromDays(30);
    public int MaxTraceRevisions { get; set; } = 10_000;
    public int MaxTracesPerBrain { get; set; } = 1_000;
    public int MaxUnsettledPerBrain { get; set; } = 100;
    public UnpricedOperationAllowance[] AllowedUnpricedOperations { get; set; } = [];

    public void Validate()
    {
        if (SettledRetention <= TimeSpan.Zero || MaxTraceRevisions <= 0 || MaxTracesPerBrain <= 0 || MaxUnsettledPerBrain <= 0)
        { throw new InvalidOperationException("Compute retention and capacity limits must be positive."); }
        if (AllowedUnpricedOperations is null || AllowedUnpricedOperations.Any(value => string.IsNullOrWhiteSpace(value.Operation) || value.MaxAttemptsPerTrace <= 0)
            || AllowedUnpricedOperations.Select(value => value.Operation).Distinct(StringComparer.Ordinal).Count() != AllowedUnpricedOperations.Length)
        { throw new InvalidOperationException("Unpriced allowances require unique declared operations and positive attempt limits."); }
    }
}

public sealed record UnpricedOperationAllowance(string Operation, int MaxAttemptsPerTrace);
