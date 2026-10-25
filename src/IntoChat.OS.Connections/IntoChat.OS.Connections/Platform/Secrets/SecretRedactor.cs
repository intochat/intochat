using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Platform.Secrets;

// Resolves the values at redaction time and keeps them only in locals: nothing is cached or logged.
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

    internal static string Apply(string text, IReadOnlyList<string> longestFirst)
    {
        foreach (var value in longestFirst) { text = text.Replace(value, Mask, StringComparison.Ordinal); }
        return text;
    }
}
