using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Connections.Platform.Secrets;

// Resolves the values at redaction time and keeps them only in locals: nothing is cached or logged.
// When invoked under an App principal (e.g. InoApp driving AgentNeuron), SecretRedactor enters the
// owner's kernel scope solely inside Values() so output masking succeeds without ever exposing raw
// secrets to apps or relaxing VaultSecretValueSource's direct zero-token guard.
internal sealed class SecretRedactor(IServiceProvider services) : ISecretRedactor
{
    internal const int MinimumLength = 8;
    internal const string Mask = "****";

    public async ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) { return text; }
        var values = await Values(cancellationToken);
        return Apply(text, values);
    }

    public async ValueTask<int> TailLength(CancellationToken cancellationToken)
    {
        var values = await Values(cancellationToken);
        return values.Count == 0 ? 0 : values[0].Length - 1;
    }

    // Longest first, so a value that contains another is masked whole.
    private async ValueTask<List<string>> Values(CancellationToken cancellationToken)
    {
        using var _ = EnterOwnerScopeIfAppCaller();
        var all = new List<string>();
        foreach (var source in services.GetServices<ISecretValueSource>())
        {
            foreach (var value in await source.CurrentValues(cancellationToken))
            {
                if (value is { Length: >= MinimumLength }) { all.Add(value); }
            }
        }
        all.Sort((left, right) => right.Length.CompareTo(left.Length));
        return all;
    }

    private static IDisposable? EnterOwnerScopeIfAppCaller()
    {
        if (!RuntimeCallerContext.TryGet(out var caller)) { return null; }
        if (caller.Kind != CallerKind.App && !AppPrincipal.IsApp(caller.PrincipalId)) { return null; }
        var owner = !string.IsNullOrEmpty(caller.AccountId) && !AppPrincipal.IsApp(caller.AccountId)
            ? caller.AccountId
            : !string.IsNullOrEmpty(caller.PrincipalId) && !AppPrincipal.IsApp(caller.PrincipalId)
                ? caller.PrincipalId
                : caller.BrainId;
        return string.IsNullOrWhiteSpace(owner) ? null : InProcessCallerScope.EnterKernel(owner);
    }

    internal static string Apply(string text, IReadOnlyList<string> longestFirst)
    {
        foreach (var value in longestFirst) { text = text.Replace(value, Mask, StringComparison.Ordinal); }
        return text;
    }
}
