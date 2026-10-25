using System.Text.Json;
using DigitalBrain.Capabilities.Secrets;
using Xunit;

namespace DigitalBrain.Testing.Conformance.Secrets;

/// <summary>A connected secrets@1 implementation under test.</summary>
/// <param name="Secrets">The secrets connection.</param>
/// <param name="Callers">Switches the runtime caller the implementation sees.</param>
/// <param name="CapturedLogs">Every log line the implementation wrote so far; none may contain a secret value.</param>
public sealed record SecretsSubject(ISecrets Secrets, CallerScope Callers, Func<IReadOnlyList<string>> CapturedLogs);

/// <summary>
/// The secrets@1 conformance kit (CAP-01). A module providing secrets subclasses it in its <c>tests/</c> project as a sealed
/// class and implements <see cref="Connect"/>; every inherited fact runs against that subject. Concurrent sets of one name
/// are required to be last-writer-wins (no ETag in the v1 surface). Assertion messages carry names, never values.
/// </summary>
/// <typeparam name="TSecrets">The implementation under test (names the subclass's subject; not otherwise used).</typeparam>
public abstract class SecretsConformance<TSecrets> where TSecrets : ISecrets
{
    /// <summary>Every fact's timeout in milliseconds.</summary>
    protected const int Timeout = 30_000;

    /// <summary>Connects to the implementation. Called once per fact.</summary>
    protected abstract Task<SecretsSubject> Connect(CancellationToken ct);

    /// <summary>The caller that owns the secrets in each fact. Override when the implementation needs a provisioned brain.</summary>
    protected virtual KitCaller Owner { get; } = new("kit-ada", "kit-ada-account", "kit-ada-brain");

    /// <summary>A caller in another brain; it must be denied the owner's secrets.</summary>
    protected virtual KitCaller Stranger { get; } = new("kit-bob", "kit-bob-account", "kit-bob-brain");

    /// <summary>Set then Resolve returns the value.</summary>
    [Fact(Timeout = Timeout)]
    public async Task SetThenResolveReturnsValue()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var value = NewValue();
        using (s.Callers.As(Owner))
        {
            var secret = await s.Secrets.Set(NewName(), "Kit key", value, ct).ConfigureAwait(false);
            Assert.True(value == await s.Secrets.Resolve(secret, ct).ConfigureAwait(false), $"{secret.Reference} did not resolve to its value");
        }
    }

    /// <summary>Set returns a set reference with the label; neither ToString nor JSON of the reference carries the value.</summary>
    [Fact(Timeout = Timeout)]
    public async Task SetReturnsRefWithNameAndNoValue()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var value = NewValue();
        var name = NewName();
        using (s.Callers.As(Owner))
        {
            var secret = await s.Secrets.Set(name, "Kit label", value, ct).ConfigureAwait(false);
            Assert.True(secret.IsSet, $"{name} is not reported set");
            Assert.Equal("Kit label", secret.Label);
            Assert.False(string.IsNullOrWhiteSpace(secret.Reference), $"{name} has no reference");
            Assert.False(Leaks(secret.ToString(), value), $"{name}: ToString carries the value");
            Assert.False(Leaks(JsonSerializer.Serialize(secret), value), $"{name}: the JSON form carries the value");
            Assert.False(Leaks(secret.Reference, value) || Leaks(secret.Label, value), $"{name}: the reference carries the value");
        }
    }

    /// <summary>Remove then Resolve throws <see cref="SecretNotFound"/> with code not-found.</summary>
    [Fact(Timeout = Timeout)]
    public async Task RemoveThenResolveIsNotFound()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var name = NewName();
        using (s.Callers.As(Owner))
        {
            var secret = await s.Secrets.Set(name, "Kit key", NewValue(), ct).ConfigureAwait(false);
            await s.Secrets.Remove(name, ct).ConfigureAwait(false);
            var error = await Assert.ThrowsAsync<SecretNotFound>(() => s.Secrets.Resolve(secret, ct)).ConfigureAwait(false);
            Assert.Equal("not-found", error.Code);
        }
    }

    /// <summary>Another brain's caller resolving the owner's reference throws <see cref="SecretDenied"/> with code denied.</summary>
    [Fact(Timeout = Timeout)]
    public async Task OtherCallersSecretIsDenied()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        SecretRef secret;
        using (s.Callers.As(Owner))
        {
            secret = await s.Secrets.Set(NewName(), "Kit key", NewValue(), ct).ConfigureAwait(false);
        }

        using (s.Callers.As(Stranger))
        {
            var error = await Assert.ThrowsAsync<SecretDenied>(() => s.Secrets.Resolve(secret, ct)).ConfigureAwait(false);
            Assert.Equal("denied", error.Code);
        }
    }

    /// <summary>Setting a name twice keeps the latest value.</summary>
    [Fact(Timeout = Timeout)]
    public async Task OverwriteKeepsLatestValue()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var name = NewName();
        var latest = NewValue();
        using (s.Callers.As(Owner))
        {
            await s.Secrets.Set(name, "Kit key", NewValue(), ct).ConfigureAwait(false);
            var secret = await s.Secrets.Set(name, "Kit key", latest, ct).ConfigureAwait(false);
            Assert.True(latest == await s.Secrets.Resolve(secret, ct).ConfigureAwait(false), $"{name} did not keep the latest value");
        }
    }

    /// <summary>Concurrent sets of one name are last-writer-wins: the stored value is one of the written values, and a later set wins.</summary>
    [Fact(Timeout = Timeout)]
    public async Task ConcurrentSetsAreLastWriterWins()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var name = NewName();
        var values = Enumerable.Range(0, 8).Select(_ => NewValue()).ToList();
        using (s.Callers.As(Owner))
        {
            var refs = await Task.WhenAll(values.Select(value => s.Secrets.Set(name, "Kit key", value, ct))).ConfigureAwait(false);
            var stored = await s.Secrets.Resolve(refs[0], ct).ConfigureAwait(false);
            Assert.True(values.Contains(stored), $"{name} holds a value no writer wrote");
            var last = NewValue();
            var secret = await s.Secrets.Set(name, "Kit key", last, ct).ConfigureAwait(false);
            Assert.True(last == await s.Secrets.Resolve(secret, ct).ConfigureAwait(false), $"{name}: the later set did not win");
        }
    }

    /// <summary>A 256 KiB value round-trips unchanged.</summary>
    [Fact(Timeout = Timeout)]
    public async Task LargeValueRoundTrips()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var value = string.Concat(Enumerable.Repeat(NewValue(), 1 + (256 * 1024 / 40)));
        using (s.Callers.As(Owner))
        {
            var secret = await s.Secrets.Set(NewName(), "Kit large", value, ct).ConfigureAwait(false);
            var resolved = await s.Secrets.Resolve(secret, ct).ConfigureAwait(false);
            Assert.True(value == resolved, $"{secret.Reference}: the large value changed ({value.Length} chars in, {resolved.Length} out)");
        }
    }

    /// <summary>Not-found and denied errors carry the name only: neither message nor ToString holds the value.</summary>
    [Fact(Timeout = Timeout)]
    public async Task ErrorsNeverCarryTheValue()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var value = NewValue();
        var name = NewName();
        SecretRef secret;
        using (s.Callers.As(Owner))
        {
            secret = await s.Secrets.Set(name, "Kit key", value, ct).ConfigureAwait(false);
        }

        using (s.Callers.As(Stranger))
        {
            var denied = await Assert.ThrowsAsync<SecretDenied>(() => s.Secrets.Resolve(secret, ct)).ConfigureAwait(false);
            Assert.False(Leaks(denied.ToString(), value), $"{name}: the denied error carries the value");
        }

        using (s.Callers.As(Owner))
        {
            await s.Secrets.Remove(name, ct).ConfigureAwait(false);
            var missing = await Assert.ThrowsAsync<SecretNotFound>(() => s.Secrets.Resolve(secret, ct)).ConfigureAwait(false);
            Assert.False(Leaks(missing.ToString(), value), $"{name}: the not-found error carries the value");
        }
    }

    /// <summary>After set, resolve, denied resolve and remove, no captured log line contains the value (T-05-05-01).</summary>
    [Fact(Timeout = Timeout)]
    public async Task LogsNeverContainValue()
    {
        var (s, ct) = await Open().ConfigureAwait(false);
        var value = NewValue();
        var name = NewName();
        SecretRef secret;
        using (s.Callers.As(Owner))
        {
            secret = await s.Secrets.Set(name, "Kit key", value, ct).ConfigureAwait(false);
            await s.Secrets.Resolve(secret, ct).ConfigureAwait(false);
        }

        using (s.Callers.As(Stranger))
        {
            await Assert.ThrowsAsync<SecretDenied>(() => s.Secrets.Resolve(secret, ct)).ConfigureAwait(false);
        }

        using (s.Callers.As(Owner))
        {
            await s.Secrets.Remove(name, ct).ConfigureAwait(false);
        }

        var lines = s.CapturedLogs();
        var leaking = lines.Count(line => Leaks(line, value));
        Assert.True(leaking == 0, $"{name}: {leaking} of {lines.Count} captured log lines contain the value");
    }

    private async Task<(SecretsSubject Subject, CancellationToken Ct)> Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await Connect(ct).ConfigureAwait(false);
        Assert.NotNull(subject);
        return (subject, ct);
    }

    private static string NewName() => $"kit-{Guid.NewGuid():N}";

    private static string NewValue() => $"v-{Guid.NewGuid():N}";

    private static bool Leaks(string? text, string value) => text is not null && text.Contains(value, StringComparison.Ordinal);
}
