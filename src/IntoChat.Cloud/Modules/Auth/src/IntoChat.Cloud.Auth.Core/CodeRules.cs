namespace IntoChat.Cloud.Auth.Core;

/// <summary>W4 D1 email-code rules. Defaults are the shipped values; options bind later.</summary>
public sealed record CodeRules
{
    public int MaxAttempts { get; init; } = 3;

    public int HourlySendCap { get; init; } = 5;

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan ResendCooldown { get; init; } = TimeSpan.FromSeconds(42);

    public TimeSpan SendCapWindow { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan ResetTicketLifetime { get; init; } = TimeSpan.FromMinutes(10);
}