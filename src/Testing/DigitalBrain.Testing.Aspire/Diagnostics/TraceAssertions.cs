namespace DigitalBrain.Testing.Aspire.Diagnostics;

public static class TraceAssertions
{
    public static string TextOf(CapturedSpan span)
        => string.Join("\n", span.Attributes.Select(pair => pair.Key + "=" + pair.Value));

    public static string TextOf(CapturedLog log)
        => log.Body + "\n" + string.Join("\n", log.Attributes.Select(pair => pair.Key + "=" + pair.Value));

    public static bool IsGenAiSpan(CapturedSpan span)
        => span.Scope.StartsWith("DigitalBrain.AI", StringComparison.Ordinal)
            || span.Scope.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal)
            || span.Scope.StartsWith("Experimental.Microsoft.Extensions.AI", StringComparison.Ordinal);

    public static Task WaitForAsync(Func<bool> condition, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? what = null)
        => WaitForAsync(condition, TimeSpan.FromSeconds(20), ct, what);

    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? what = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            { throw new TimeoutException($"Waited {timeout.TotalSeconds:F0}s for: {what}"); }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
